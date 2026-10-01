[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DistributionName,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$RequiredAdditionalBytes
)

# Read-only host provisioning preflight. Does not start WSL, inspect guest data,
# resize/compact a VHD, remove evidence or authorize a target operation.
$ErrorActionPreference = 'Stop'
try {
    $distributions = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss' |
        ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } |
        Where-Object { $_.DistributionName -ceq $DistributionName })
    if ($distributions.Count -ne 1 -or $distributions[0].Version -ne 2) {
        throw 'DistributionBackingUnavailable'
    }
    $distribution = $distributions[0]
    $base = [string]$distribution.BasePath
    $name = [string]$distribution.VhdFileName
    if ([string]::IsNullOrEmpty($name)) { $name = 'ext4.vhdx' }
    if ($name -notmatch '^[A-Za-z0-9_.-]+\.vhdx$') { throw 'BackingNameUnsupported' }
    if ($base.StartsWith('\\?\')) { $base = $base.Substring(4) }
    if ($base -notmatch '^[A-Za-z]:\\') { throw 'BackingPlacementUnsupported' }
    $backing = Get-Item -LiteralPath (Join-Path $base $name)
    if ($backing.PSIsContainer) { throw 'BackingNotRegularFile' }
    $item = $backing
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'BackingPlacementReparsePoint'
        }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($backing.FullName))
    if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw 'BackingVolumeUnavailable'
    }
    $available = $drive.AvailableFreeSpace
    $sufficient = $available -ge $RequiredAdditionalBytes
    [ordered]@{
        SchemaVersion = 1
        Scope = 'HostCapacityObservationOnly'
        Distribution = $DistributionName
        BackingVolume = $drive.Name
        AvailableBytes = $available
        RequiredAdditionalBytes = $RequiredAdditionalBytes
        ObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Sufficient = $sufficient
        NativeMutationAuthorized = $false
    } | ConvertTo-Json -Compress
    if (-not $sufficient) { exit 2 }
} catch {
    # Exception messages may contain private registry/backing paths.
    [ordered]@{ SchemaVersion = 1; Scope = 'HostCapacityObservationOnly';
        Sufficient = $false; Code = 'HostBackingCapacityUnavailable';
        NativeMutationAuthorized = $false } | ConvertTo-Json -Compress
    exit 3
}
