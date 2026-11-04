# Disposable, file-backed VHDX probe of the actual reviewed WMI primitives.
# The fixed image path and BusType 15 check deliberately exclude physical disks.
$ErrorActionPreference = 'Stop'
$ImagePath = 'C:\Users\Public\igloo-reviewed-vhd-20261017\lab-e4d18e3ce1114453a49010295c8e3757.vhdx'
$ExpectedBytes = [uint64]42949672960
$ExpectedRoot = 'C:\Users\Public\igloo-reviewed-vhd-20261017'
$KnownGptGuid = '{31d1697a-520e-4f7b-84fd-25475707ace8}'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$groups = whoami /groups | Out-String
if (-not $admin -or $groups -notmatch 'S-1-5-32-544.+Enabled group' -or
    $groups -notmatch 'S-1-16-12288' -or $groups -match 'S-1-16-8192') {
    throw 'The executing PowerShell process does not have an enabled Administrator group and High integrity.'
}
$actualFile = Get-Item -LiteralPath $ImagePath
if ($actualFile.FullName -cne $ImagePath -or
    $actualFile.Directory.FullName -cne $ExpectedRoot -or
    ($actualFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The exact disposable VHDX path changed or is redirected.'
}

function Read-ExactVhdDisk([string]$style, [string]$gptGuid = '') {
    $file = Get-Item -LiteralPath $ImagePath
    if ($file.FullName -cne $ImagePath -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The disposable VHDX path changed.'
    }
    $image = Get-DiskImage -ImagePath $ImagePath
    if ($image.ImagePath -cne $ImagePath -or -not $image.Attached -or
        [uint64]$image.Size -ne $ExpectedBytes) {
        throw 'The image path, attachment, or virtual size changed.'
    }
    $disks = @($image | Get-Disk)
    if ($disks.Count -ne 1) { throw 'The image does not map to exactly one disk.' }
    $disk = $disks[0]
    if ([string]$disk.BusType -cne 'File Backed Virtual' -or
        [uint64]$disk.Size -ne $ExpectedBytes -or
        $disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or $disk.IsOffline -or
        [string]$disk.PartitionStyle -cne $style) {
        throw 'The image maps to a changed, protected, or physical disk.'
    }
    if ($gptGuid -and [string]$disk.Guid -ine $gptGuid) {
        throw 'The VHDX GPT identity changed.'
    }
    return $disk
}

$log = Join-Path $ExpectedRoot ('reviewed-wmi-probe-' +
    (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    [guid]::NewGuid().ToString('N') + '.log')
Start-Transcript -LiteralPath $log | Out-Null
$attachedHere = $false
try {
    Write-Output "Administrator=$admin"
    whoami
    $groups.Split([Environment]::NewLine) | Where-Object {
        $_ -match 'S-1-5-32-544|S-1-16-12288'
    }
    $before = Get-DiskImage -ImagePath $ImagePath
    if ($before.Attached -or $before.ImagePath -cne $ImagePath -or
        [uint64]$before.Size -ne $ExpectedBytes) {
        throw 'The disposable VHDX was already attached or changed.'
    }
    $base = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Add-Type -Path (Join-Path $base 'src\Igloo.Core\bin\Debug\net8.0\Igloo.Core.dll')
    Add-Type -Path (Join-Path $base 'src\Igloo.Preflight\bin\Debug\net8.0-windows10.0.19041.0\Igloo.Preflight.dll')
    $type = [Igloo.Preflight.WindowsStorageReader].Assembly.GetType(
        'Igloo.Preflight.CommunityPreparation.ReviewedPartitionCreationService', $true)
    $flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
    $create = $type.GetMethod('CreateExactViaWmi', $flags)
    $format = $type.GetMethod('FormatExactViaWmi', $flags)
    if ($null -eq $create -or $null -eq $format) {
        throw 'The reviewed WMI primitives are unavailable.'
    }
    Mount-DiskImage -ImagePath $ImagePath -NoDriveLetter | Out-Null
    $attachedHere = $true
    $attached = @((Get-DiskImage -ImagePath $ImagePath) | Get-Disk)
    if ($attached.Count -ne 1) { throw 'The attached image disk is ambiguous.' }
    if ([string]$attached[0].PartitionStyle -ceq 'RAW') {
        $raw = Read-ExactVhdDisk 'RAW'
        Write-Output "Attached exact VHDX disk=$($raw.Number) bus=$($raw.BusType) size=$($raw.Size) style=$($raw.PartitionStyle)"
        $rawBefore = Read-ExactVhdDisk 'RAW'
        Initialize-Disk -Number $rawBefore.Number -PartitionStyle GPT -PassThru | Out-Null
    } elseif ([string]$attached[0].PartitionStyle -cne 'GPT') {
        throw 'The disposable VHDX is neither RAW nor GPT.'
    } elseif ([string]$attached[0].Guid -ine $KnownGptGuid) {
        throw 'The detached GPT checkpoint is not the one created by this probe.'
    }
    $gpt = Read-ExactVhdDisk 'GPT'
    $gptGuid = [string]$gpt.Guid
    if ([string]::IsNullOrWhiteSpace($gptGuid)) { throw 'The initialized VHDX has no GPT GUID.' }
    Write-Output "Initialized exact VHDX GPT=$gptGuid"

    $offset = [uint64](256 * 1024 * 1024)
    $size = [uint64](1024 * 1024 * 1024)
    $end = $offset + $size
    foreach ($part in @(Get-Partition -DiskNumber $gpt.Number -ErrorAction SilentlyContinue)) {
        if ([uint64]$part.Offset -lt $end -and
            ([uint64]$part.Offset + [uint64]$part.Size) -gt $offset) {
            throw 'The planned disposable partition overlaps an existing GPT entry.'
        }
    }
    $reader = [Igloo.Preflight.WindowsStorageReader]::new()
    $rows = @($reader.ReadDisks().RowsOrThrow() | Where-Object {
        [uint32]$_.Properties['Number'] -eq [uint32]$gpt.Number
    })
    if ($rows.Count -ne 1 -or [int]$rows[0].Properties['BusType'] -ne 15 -or
        [uint64]$rows[0].Properties['Size'] -ne $ExpectedBytes -or
        [string]$rows[0].Properties['Guid'] -ine $gptGuid) {
        throw 'The WMI disk row does not match the exact file-backed GPT disk.'
    }
    $espType = [guid]'c12a7328-f81f-11d2-ba4b-00a0c93ec93b'
    $allocation = [Igloo.Core.Preparation.PreparationAllocationV1]::new(
        [Igloo.Core.Preparation.PreparationRole]::LinuxEsp,
        $offset, $size, $espType, 'FAT32', $true)
    $null = Read-ExactVhdDisk 'GPT' $gptGuid
    $createArgs = [object[]]::new(3)
    $createArgs[0] = $rows[0].PSObject.BaseObject
    $createArgs[1] = $allocation.PSObject.BaseObject
    $createArgs[2] = [Threading.CancellationToken]::None
    try {
        $result = $create.Invoke($null, $createArgs)
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
    Write-Output "CreatePartition ReturnValue=$($result.ReturnValue) CreatedPartition=$($null -ne $result.CreatedPartition)"
    if ([uint32]$result.ReturnValue -ne 0 -or $null -eq $result.CreatedPartition) {
        throw 'Native CreatePartition did not provide an exact success receipt; no format attempted.'
    }
    $createdGuid = [guid]$result.CreatedPartition.Properties['Guid']
    $createdDisk = Read-ExactVhdDisk 'GPT' $gptGuid
    $partitions = @(Get-Partition -DiskNumber $createdDisk.Number)
    $matches = @($partitions | Where-Object {
        [guid]$_.Guid -eq $createdGuid -and [uint64]$_.Offset -eq $offset -and
            [uint64]$_.Size -eq $size -and [guid]$_.GptType -eq $espType
    })
    if ($matches.Count -ne 1) {
        throw 'The created VHDX GPT partition failed exact readback; no format attempted.'
    }
    $freshRows = @($reader.ReadPartitions([int]$createdDisk.Number).RowsOrThrow() | Where-Object {
        [guid]$_.Properties['Guid'] -eq $createdGuid
    })
    if ($freshRows.Count -ne 1 -or
        [uint64]$freshRows[0].Properties['Offset'] -ne $offset -or
        [uint64]$freshRows[0].Properties['Size'] -ne $size) {
        throw 'The created partition has no unique fresh WMI row; no format attempted.'
    }
    $null = Read-ExactVhdDisk 'GPT' $gptGuid
    $formatArgs = [object[]]::new(3)
    $formatArgs[0] = $freshRows[0].PSObject.BaseObject
    $formatArgs[1] = $allocation.PSObject.BaseObject
    $formatArgs[2] = [Threading.CancellationToken]::None
    try {
        $formatCode = $format.Invoke($null, $formatArgs)
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
    Write-Output "Format ReturnValue=$formatCode"
    if ([uint32]$formatCode -ne 0) { throw 'MSFT_Volume.Format did not succeed.' }
    $formattedDisk = Read-ExactVhdDisk 'GPT' $gptGuid
    $formatted = @(Get-Partition -DiskNumber $formattedDisk.Number | Where-Object {
        [guid]$_.Guid -eq $createdGuid
    })
    if ($formatted.Count -ne 1 -or [uint64]$formatted[0].Offset -ne $offset -or
        [uint64]$formatted[0].Size -ne $size) {
        throw 'The formatted GPT partition changed.'
    }
    $volumes = @($formatted[0] | Get-Volume -ErrorAction Stop)
    if ($volumes.Count -ne 1 -or $volumes[0].FileSystem -ne 'FAT32' -or
        $volumes[0].FileSystemLabel -ne 'IGLOOESP') {
        throw 'The formatted VHDX partition lacks one exact FAT32 volume.'
    }
    Write-Output "Readback GPT=$gptGuid partition=$createdGuid offset=$offset size=$size filesystem=$($volumes[0].FileSystem) label=$($volumes[0].FileSystemLabel)"
} finally {
    if ($attachedHere) {
        $current = Get-DiskImage -ImagePath $ImagePath
        if ($current.ImagePath -cne $ImagePath -or -not $current.Attached -or
            [uint64]$current.Size -ne $ExpectedBytes) {
            throw 'The exact VHDX cannot be safely detached after the probe.'
        }
        $detaching = @($current | Get-Disk)
        if ($detaching.Count -ne 1 -or
            [string]$detaching[0].BusType -cne 'File Backed Virtual' -or
            [uint64]$detaching[0].Size -ne $ExpectedBytes -or
            $detaching[0].IsBoot -or $detaching[0].IsSystem) {
            throw 'The attached VHDX identity changed before detach.'
        }
        Dismount-DiskImage -ImagePath $ImagePath | Out-Null
        Write-Output "Detached exact VHDX; allocated file bytes=$((Get-Item -LiteralPath $ImagePath).Length)"
    }
    Stop-Transcript | Out-Null
}
