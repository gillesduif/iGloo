<#
.SYNOPSIS
  Collects the evidence of one iGloo Community run inside the Windows guest. Reads only.

.DESCRIPTION
  Writes one folder per call below <OutputRoot>\<Run>-<date>\ and nothing anywhere else;
  the temporary files of this process and of Check-BootRegistration.ps1 land in that folder.
  No file on an installer partition is opened. No manifest, staged file or ISO is copied.
  Copied logs pass a filter that replaces password hashes and key values.
  Run as Administrator, from Windows PowerShell 5.1 or later.

.PARAMETER Run
  Name of the run, for example r6-debian.

.PARAMETER Phase
  before    clean snapshot, before the wizard starts
  prepared  the wizard shows "Ready to reboot" and the restart is cancelled (shutdown /a)
  after     back in Windows after the Linux installation
  error     the wizard stopped with an error
  refused   the wizard refused before it changed the disk
  removed   after a removal in System Check

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Collect-WindowsEvidence.ps1 -Run r6-debian -Phase prepared
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$Run,
    [ValidateSet('before', 'prepared', 'after', 'error', 'refused', 'removed')][string]$Phase = 'prepared',
    [string]$OutputRoot = (Join-Path $env:SystemDrive 'igloo-evidence'),
    [string]$TestDataPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'igloo-testdata'),
    [string]$IglooData = (Join-Path $env:LOCALAPPDATA 'Igloo'),
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
$seedLabels = 'OEMDRV', 'CIDATA', 'IGLOOISO'
$maxLogBytes = 64MB

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator (bcdedit and UEFI variable reads need it).'
}

function Test-Below([string]$Path, [string]$Parent) {
    $a = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $b = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $a.StartsWith($b, [StringComparison]::OrdinalIgnoreCase)
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Below $OutputRoot $IglooData) { throw "The output folder must not be inside $IglooData." }
if (Test-Below $OutputRoot $TestDataPath) { throw "The output folder must not be inside $TestDataPath." }
$rootLabel = ''
try { $rootLabel = (New-Object IO.DriveInfo([IO.Path]::GetPathRoot($OutputRoot))).VolumeLabel } catch { }
if ($seedLabels -contains $rootLabel) { throw "The output folder must not be on an installer partition ($rootLabel)." }

$taken = Get-Date
$runDir = Join-Path $OutputRoot ('{0}-{1:yyyyMMdd}' -f $Run, $taken)
$out = Join-Path $runDir ('windows-{0}-{1:HHmmss}' -f $Phase, $taken)
$tmp = Join-Path $out 'tmp'
[void][IO.Directory]::CreateDirectory($tmp)
$env:TMP = $tmp
$env:TEMP = $tmp
$env:PSModuleAnalysisCachePath = Join-Path $tmp 'ModuleAnalysisCache'

$problems = New-Object System.Collections.Generic.List[string]
$facts = New-Object System.Collections.Generic.List[string]
$utf8 = New-Object Text.UTF8Encoding($false)

function Protect-Text([string]$Text) {
    if (-not $Text) { return $Text }
    $t = [regex]::Replace($Text, '\$(?:1|2[abxy]?|5|6|7|y|gy|argon2(?:id|i|d))\$[^\s"''\\,;]+', '[REDACTED-HASH]')
    return [regex]::Replace($t,
        '(?i)((?:psk|password|passwd|passphrase|pwd|secret)[\w-]*"?\s*[=:]\s*)(?!(?:\d{1,6}|true|false|null|yes|no|none)\b|\[REDACTED)("[^"]*"|''[^'']*''|\S[^\r\n]*)',
        '$1[REDACTED]')
}

function Save-Text([string]$Name, $Content) {
    $path = Join-Path $script:out $Name
    [void][IO.Directory]::CreateDirectory((Split-Path $path -Parent))
    [IO.File]::WriteAllText($path, (Protect-Text ([string]$Content)), $script:utf8)
}

function Invoke-Section([string]$Name, [scriptblock]$Body) {
    Write-Host "... $Name"
    try { & $Body } catch { $script:problems.Add(('{0}: {1}' -f $Name, $_.Exception.Message)) }
}

function Format-List2($Objects) { return ($Objects | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd() }

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $text = (& $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Out-String)
    return ("> {0} {1}`r`n(exit code {2})`r`n{3}" -f $Exe, ($Arguments -join ' '), $LASTEXITCODE, $text)
}

function Read-SharedText([string]$Path, [long]$MaxBytes) {
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]'ReadWrite, Delete'))
    try {
        if ($stream.Length -gt $MaxBytes) { [void]$stream.Seek(-$MaxBytes, [IO.SeekOrigin]::End) }
        return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)).ReadToEnd()
    } finally { $stream.Dispose() }
}

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]'ReadWrite, Delete'), 1MB)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Walks a tree without following links; the \\?\ prefix keeps paths over 260 characters readable.
function Get-Tree([string]$Root) {
    $plain = $Root.TrimEnd('\')
    if ($plain -match '^[A-Za-z]:$') { $plain += '\' }
    $start = '\\?\' + $plain
    try { [void](New-Object IO.DirectoryInfo($start)).Exists } catch { $start = $plain }
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($start)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        $entries = $null
        try { $entries = (New-Object IO.DirectoryInfo($dir)).GetFileSystemInfos() }
        catch { [pscustomobject]@{ Kind = 'E'; Path = $dir.Substring($start.Length).TrimStart('\'); Size = 0; Time = $null; Full = $dir }; continue }
        foreach ($entry in $entries) {
            $relative = $entry.FullName.Substring($start.Length).TrimStart('\')
            $isDirectory = ($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0
            $isLink = ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            if ($isLink) { [pscustomobject]@{ Kind = 'L'; Path = $relative; Size = 0; Time = $entry.LastWriteTimeUtc; Full = $entry.FullName } }
            elseif ($isDirectory) {
                [pscustomobject]@{ Kind = 'D'; Path = $relative; Size = 0; Time = $entry.LastWriteTimeUtc; Full = $entry.FullName }
                $stack.Push($entry.FullName)
            }
            else { [pscustomobject]@{ Kind = 'F'; Path = $relative; Size = $entry.Length; Time = $entry.LastWriteTimeUtc; Full = $entry.FullName } }
        }
    }
}

function Format-Tree($Items) {
    $lines = foreach ($item in $Items) {
        $size = switch ($item.Kind) { 'F' { '{0,14}' -f $item.Size } 'D' { '{0,14}' -f '<DIR>' } 'L' { '{0,14}' -f '<LINK>' } default { '{0,14}' -f '<UNREADABLE>' } }
        $time = ''
        if ($item.Time) { $time = $item.Time.ToString('yyyy-MM-dd HH:mm:ss') }
        '{0}  {1,-19}  {2}' -f $size, $time, $item.Path
    }
    $sorted = [string[]]@($lines)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return ($sorted -join "`r`n")
}

function Get-Letter($Partition) {
    if ($Partition.DriveLetter -and [int][char]$Partition.DriveLetter -ne 0) { return [string]$Partition.DriveLetter }
    return ''
}

# ---- Machine -----------------------------------------------------------------------
Invoke-Section 'machine' {
    $cs = Get-CimInstance Win32_ComputerSystem
    $os = Get-CimInstance Win32_OperatingSystem
    $bios = Get-CimInstance Win32_BIOS
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('taken (local): {0:yyyy-MM-dd HH:mm:ss zzz}' -f $taken))
    $lines.Add(('computer: {0} {1}; firmware {2} {3}' -f $cs.Manufacturer, $cs.Model, $bios.Manufacturer, $bios.SMBIOSBIOSVersion))
    $lines.Add(('memory: {0:N0} MiB; logical processors: {1}' -f ($cs.TotalPhysicalMemory / 1MB), $cs.NumberOfLogicalProcessors))
    $lines.Add(('windows: {0} {1} build {2}' -f $os.Caption, $os.Version, $os.BuildNumber))
    $lines.Add(('user: {0}; profile: {1}' -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, $env:USERPROFILE))
    $secureBoot = 'unknown'
    try { $secureBoot = [string](Confirm-SecureBootUEFI) } catch { $secureBoot = 'not readable: ' + $_.Exception.Message }
    $lines.Add("secure boot: $secureBoot")
    try {
        $tpm = Get-Tpm
        $spec = (Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue).SpecVersion
        $lines.Add(('tpm: present={0} ready={1} spec={2}' -f $tpm.TpmPresent, $tpm.TpmReady, $spec))
    } catch { $lines.Add('tpm: not readable: ' + $_.Exception.Message) }
    $marker = 'not set'
    try {
        $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64').OpenSubKey('SOFTWARE\Policies\iGloo', $false)
        if ($key) { $value = $key.GetValue('ProtectedHost'); if ($null -ne $value) { $marker = "SET ($value)" }; $key.Dispose() }
    } catch { $marker = 'not readable: ' + $_.Exception.Message }
    $lines.Add("protected-host marker: $marker")
    $power = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ErrorAction SilentlyContinue
    $lines.Add(('fast startup (HiberbootEnabled): {0}' -f $power.HiberbootEnabled))
    $rtc = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation' -ErrorAction SilentlyContinue).RealTimeIsUniversal
    $lines.Add(('RealTimeIsUniversal: {0}; time zone: {1}' -f $rtc, (Get-TimeZone).Id))
    $lines.Add(('culture: {0}; ui culture: {1}; system locale: {2}; home location: {3}' -f (Get-Culture).Name, (Get-UICulture).Name, (Get-WinSystemLocale).Name, (Get-WinHomeLocation).HomeLocation))
    $languages = foreach ($language in (Get-WinUserLanguageList)) { '{0} [{1}]' -f $language.LanguageTag, ($language.InputMethodTips -join ',') }
    $lines.Add('languages: ' + ($languages -join '; '))
    foreach ($line in $lines) { $script:facts.Add($line) }
    Save-Text 'machine.txt' (($lines -join "`r`n") + "`r`n`r`n" + (Invoke-Native 'manage-bde.exe' @('-status')) + "`r`n" + (Invoke-Native 'powercfg.exe' @('/a')))
}

# ---- Installed build -----------------------------------------------------------------
Invoke-Section 'installed build' {
    $uninstall = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7B4E9C2A-6F3D-4A1B-9E5C-2D8F0A3B6C71}_is1' -ErrorAction SilentlyContinue
    if (-not $InstallDir -and $uninstall) { $script:InstallDir = $uninstall.InstallLocation }
    if (-not $script:InstallDir) { $script:InstallDir = Join-Path $env:ProgramFiles 'iGloo' }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(('install directory: {0}; setup version: {1}; installed: {2}' -f $script:InstallDir, $uninstall.DisplayVersion, $uninstall.InstallDate))
    if (Test-Path -LiteralPath $script:InstallDir) {
        $binaries = Get-ChildItem -LiteralPath $script:InstallDir -Recurse -File | Where-Object { $_.Name -match '^(?i)igloo.*\.(exe|dll)$' } | Sort-Object FullName
        foreach ($binary in $binaries) {
            $lines.Add(('{0}  {1,12}  {2}' -f (Get-Sha256 $binary.FullName), $binary.Length, $binary.FullName.Substring($script:InstallDir.TrimEnd('\').Length + 1)))
        }
        $exe = Join-Path $script:InstallDir 'iGloo.exe'
        if (Test-Path -LiteralPath $exe) { $script:facts.Add(('installed iGloo.exe: {0} sha256 {1}' -f (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion, (Get-Sha256 $exe))) }
    } else { $script:facts.Add("installed build: $($script:InstallDir) not found") }
    $running = @(Get-Process -Name iGloo -ErrorAction SilentlyContinue)
    $lines.Add(('iGloo processes running: {0}' -f $running.Count))
    Save-Text 'installed-build.txt' ($lines -join "`r`n")
}

# ---- Disks ---------------------------------------------------------------------------
$partitions = @()
Invoke-Section 'disks' {
    $script:partitions = @(foreach ($p in (Get-Partition | Sort-Object DiskNumber, Offset)) {
        $v = $null
        try { $v = $p | Get-Volume -ErrorAction Stop } catch { }
        [pscustomobject]@{
            Disk = $p.DiskNumber; Number = $p.PartitionNumber; Letter = (Get-Letter $p); Offset = $p.Offset; Size = $p.Size
            Type = $p.Type; GptType = $p.GptType; Guid = $p.Guid; IsSystem = $p.IsSystem; IsBoot = $p.IsBoot; IsHidden = $p.IsHidden
            FileSystem = $(if ($v) { $v.FileSystem } else { '' }); Label = $(if ($v) { $v.FileSystemLabel } else { '' })
            VolumeSize = $(if ($v) { $v.Size } else { 0 }); VolumeFree = $(if ($v) { $v.SizeRemaining } else { 0 })
        }
    })
    $disks = @(Get-Disk | Sort-Object Number)
    $free = foreach ($d in $disks) {
        $cursor = [long]1MB
        foreach ($p in ($script:partitions | Where-Object { $_.Disk -eq $d.Number } | Sort-Object Offset)) {
            if ($p.Offset - $cursor -ge 1MB) { [pscustomobject]@{ Disk = $d.Number; Offset = $cursor; Size = $p.Offset - $cursor; GiB = [Math]::Round(($p.Offset - $cursor) / 1GB, 2) } }
            $cursor = [Math]::Max($cursor, [long]($p.Offset + $p.Size))
        }
        $end = [long]$d.Size - 1MB
        if ($end - $cursor -ge 1MB) { [pscustomobject]@{ Disk = $d.Number; Offset = $cursor; Size = $end - $cursor; GiB = [Math]::Round(($end - $cursor) / 1GB, 2) } }
    }
    $text = "Get-Disk`r`n" + (Format-List2 ($disks | Select-Object Number, FriendlyName, BusType, PartitionStyle, Guid, Size, AllocatedSize, LargestFreeExtent, IsBoot, IsSystem)) +
        "`r`n`r`nPartitions (bytes)`r`n" + (Format-List2 $script:partitions) +
        "`r`n`r`nUnallocated regions of 1 MiB or more, computed from the partition offsets`r`n" + (Format-List2 $free) +
        "`r`n`r`nGet-Volume`r`n" + (Format-List2 (Get-Volume | Select-Object DriveLetter, FileSystemLabel, FileSystem, DriveType, HealthStatus, Size, SizeRemaining, Path))
    $windows = $script:partitions | Where-Object { $_.Letter -eq $env:SystemDrive.TrimEnd(':') } | Select-Object -First 1
    if ($windows) {
        try {
            $supported = Get-PartitionSupportedSize -DriveLetter $windows.Letter
            $text += ("`r`n`r`nWindows partition {0}: on disk {1}; smallest size Windows reports {2:N0} bytes; shrinkable now {3:N2} GiB" -f $windows.Letter, $windows.Disk, $supported.SizeMin, (($windows.Size - $supported.SizeMin) / 1GB))
        } catch { $text += "`r`n`r`nShrink size of the Windows partition not readable: $($_.Exception.Message)" }
        $onDisk = @($script:partitions | Where-Object { $_.Disk -eq $windows.Disk })
        $regions = @($free | Where-Object { $_.Disk -eq $windows.Disk -and $_.Size -ge 1GB })
        $esp = @($onDisk | Where-Object { $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' })
        $script:facts.Add(('windows disk {0}: {1} partitions, {2} EFI system partition(s), {3} unallocated region(s) of 1 GiB or more [{4}]' -f $windows.Disk, $onDisk.Count, $esp.Count, $regions.Count, (($regions | ForEach-Object { '{0} GiB at {1}' -f $_.GiB, $_.Offset }) -join '; ')))
        $script:facts.Add(('windows partition {0}: {1:N0} bytes; disks: {2}' -f $windows.Letter, $windows.Size, $disks.Count))
    }
    Save-Text 'disks.txt' $text
    Save-Text 'partitions.json' ($script:partitions | ConvertTo-Json -Depth 3)
}

# ---- Windows volume health: reads only -------------------------------------------------
Invoke-Section 'volume health' {
    $letter = $env:SystemDrive.TrimEnd(':')
    $volume = Get-Volume -DriveLetter $letter
    $text = "Get-Volume`r`n" + (Format-List2 ($volume | Select-Object DriveLetter, FileSystem, HealthStatus, OperationalStatus, Size, SizeRemaining)) +
        "`r`n`r`n" + (Invoke-Native 'fsutil.exe' @('dirty', 'query', $env:SystemDrive)) +
        "`r`n`r`n" + (Invoke-Native 'fsutil.exe' @('repair', 'state', $env:SystemDrive))
    $since = (Get-Date).AddDays(-7)
    $events = @()
    foreach ($source in @(@('System', 'Ntfs'), @('System', 'Microsoft-Windows-Ntfs'), @('Application', 'Chkdsk'), @('Application', 'Microsoft-Windows-Wininit'))) {
        # A provider this Windows does not have, or one without events, is not an error here.
        try { $events += @(Get-WinEvent -FilterHashtable @{ LogName = $source[0]; ProviderName = $source[1]; StartTime = $since } -MaxEvents 40 -ErrorAction Stop) } catch { }
    }
    $lines = foreach ($entry in ($events | Sort-Object TimeCreated)) {
        '{0:yyyy-MM-dd HH:mm:ss}  {1} {2} level {3}  {4}' -f $entry.TimeCreated, $entry.ProviderName, $entry.Id, $entry.Level, ((([string]$entry.Message) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join ' | ')
    }
    $text += "`r`n`r`nNTFS and disk-check events of the last 7 days`r`n" + ($lines -join "`r`n")
    Save-Text 'volume-health.txt' $text
    $script:facts.Add(('windows volume {0}: health {1}, {2}; {3} NTFS or disk-check event(s) in 7 days, see volume-health.txt' -f $letter, $volume.HealthStatus, ($volume.OperationalStatus -join '+'), $events.Count))
}

# ---- Installer partitions: names and sizes only ----------------------------------------
Invoke-Section 'installer partitions' {
    $seeds = @($script:partitions | Where-Object { $seedLabels -contains $_.Label })
    $text = "Volumes labelled $($seedLabels -join ', ')`r`n" + (Format-List2 $seeds)
    foreach ($seed in $seeds) {
        if (-not $seed.Letter) { $text += "`r`n`r`nDisk $($seed.Disk) partition $($seed.Number) ($($seed.Label)) has no drive letter; not listed."; continue }
        $tree = @(Get-Tree ($seed.Letter + ':\'))
        $text += "`r`n`r`n$($seed.Letter): ($($seed.Label), disk $($seed.Disk) partition $($seed.Number), $($seed.Guid))`r`n" + (Format-Tree $tree)
        $top = @($tree | Where-Object { $_.Path -notmatch '\\' } | ForEach-Object { $_.Path })
        $script:facts.Add(('installer partition {0}: {1} {2}, {3:N0} bytes, {4} files; identity file={5}; reuse receipt={6}; files tree={7}; browser-profiles tree={8}' -f $seed.Letter, $seed.Label, $seed.FileSystem, $seed.Size,
            @($tree | Where-Object { $_.Kind -eq 'F' }).Count, ($top -contains 'igloo-seed-identity.json'), ($top -contains 'igloo-seed-reuse.json'), ($top -contains 'files'), ($top -contains 'browser-profiles')))
    }
    if ($seeds.Count -eq 0) { $script:facts.Add('installer partitions: none') }
    Save-Text 'installer-partitions.txt' $text
}

# ---- Boot configuration ------------------------------------------------------------------
Invoke-Section 'bcd' {
    Save-Text 'bcdedit-enum-firmware.txt' (Invoke-Native 'bcdedit.exe' @('/enum', 'firmware'))
    Save-Text 'bcdedit-enum-fwbootmgr.txt' (Invoke-Native 'bcdedit.exe' @('/enum', '{fwbootmgr}'))
    Save-Text 'bcdedit-enum-all.txt' (Invoke-Native 'bcdedit.exe' @('/enum', 'all', '/v'))
    # The wizard schedules this task at Restart; it removes itself after the cleanup.
    $task = Invoke-Native 'schtasks.exe' @('/Query', '/TN', 'iGloo boot entry cleanup', '/V', '/FO', 'LIST')
    Save-Text 'boot-cleanup-task.txt' $task
    $script:facts.Add('boot entry cleanup task: ' + $(if ($task -match '\(exit code 0\)') { 'scheduled' } else { 'not scheduled' }))
}

Invoke-Section 'uefi variables' {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IglooEvidenceEfi {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFirmwareEnvironmentVariableW(string name, string guid, byte[] buffer, uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValueW(string sys, string name, out long luid);
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokPriv { public uint Count; public long Luid; public uint Attr; }
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokPriv state, uint len, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    public static int EnablePrivilege() {
        IntPtr tok;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out tok)) return Marshal.GetLastWin32Error();
        try {
            long luid; if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out luid)) return Marshal.GetLastWin32Error();
            var tp = new TokPriv { Count = 1, Luid = luid, Attr = 2 };
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            return Marshal.GetLastWin32Error();
        } finally { CloseHandle(tok); }
    }
    public static byte[] Read(string name) {
        var buf = new byte[65536];
        var n = GetFirmwareEnvironmentVariableW(name, "{8be4df61-93ca-11d2-aa0d-00e098032b8c}", buf, (uint)buf.Length);
        if (n == 0) return null;
        var r = new byte[n]; Array.Copy(buf, r, n); return r;
    }
}
'@
    function Get-Words([byte[]]$Bytes) { $words = @(); if ($Bytes) { for ($i = 0; $i + 1 -lt $Bytes.Length; $i += 2) { $words += [BitConverter]::ToUInt16($Bytes, $i) } }; return $words }
    function Format-LoadOption([byte[]]$b) {
        if (-not $b -or $b.Length -lt 8) { return '(malformed)' }
        $attributes = [BitConverter]::ToUInt32($b, 0)
        $pathLength = [BitConverter]::ToUInt16($b, 4)
        $end = 6
        while ($end + 1 -lt $b.Length -and -not ($b[$end] -eq 0 -and $b[$end + 1] -eq 0)) { $end += 2 }
        $description = [Text.Encoding]::Unicode.GetString($b, 6, $end - 6)
        $pos = $end + 2
        $stop = [Math]::Min($b.Length, $pos + $pathLength)
        $nodes = @()
        while ($pos + 4 -le $stop) {
            $type = $b[$pos]; $sub = $b[$pos + 1]; $length = [BitConverter]::ToUInt16($b, $pos + 2)
            if ($length -lt 4 -or $pos + $length -gt $stop) { $nodes += 'malformed node'; break }
            if ($type -eq 4 -and $sub -eq 1 -and $length -ge 42) {
                $signature = New-Object byte[] 16
                [Array]::Copy($b, $pos + 24, $signature, 0, 16)
                $id = [BitConverter]::ToString($signature)
                if ($b[$pos + 41] -eq 2) { $id = (New-Object Guid (, $signature)).ToString() }
                $nodes += ('HD(partition={0},start={1},sectors={2},signature={3})' -f [BitConverter]::ToUInt32($b, $pos + 4), [BitConverter]::ToUInt64($b, $pos + 8), [BitConverter]::ToUInt64($b, $pos + 16), $id)
            }
            elseif ($type -eq 4 -and $sub -eq 4) { $nodes += ('File(' + [Text.Encoding]::Unicode.GetString($b, $pos + 4, $length - 4).TrimEnd([char]0) + ')') }
            elseif ($type -eq 0x7F) { $pos += $length; break }
            else { $nodes += ('Node(type={0},subtype={1},bytes={2})' -f $type, $sub, $length) }
            $pos += $length
        }
        $optionalStart = $end + 2 + $pathLength
        $optional = ''
        if ($optionalStart -lt $b.Length) {
            $raw = [Text.Encoding]::Unicode.GetString($b, $optionalStart, ($b.Length - $optionalStart) - (($b.Length - $optionalStart) % 2))
            $optional = [regex]::Replace($raw, '[^\u0020-\u007E]', '.')
        }
        return ("'{0}' attributes=0x{1:X8} path={2} optional[{3} bytes]={4}" -f $description, $attributes, ($nodes -join '/'), [Math]::Max(0, $b.Length - $optionalStart), $optional)
    }
    $privilege = [IglooEvidenceEfi]::EnablePrivilege()
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("SeSystemEnvironmentPrivilege: Win32 error $privilege (0 = enabled)")
    $order = @(Get-Words ([IglooEvidenceEfi]::Read('BootOrder')))
    $next = @(Get-Words ([IglooEvidenceEfi]::Read('BootNext')))
    $current = @(Get-Words ([IglooEvidenceEfi]::Read('BootCurrent')))
    $lines.Add('BootOrder:   ' + (($order | ForEach-Object { $_.ToString('X4') }) -join ','))
    $lines.Add('BootNext:    ' + $(if ($next.Count) { $next[0].ToString('X4') } else { 'not set' }))
    $lines.Add('BootCurrent: ' + $(if ($current.Count) { $current[0].ToString('X4') } else { 'not readable' }))
    $indexes = @(0..0xFF) + @($order | ForEach-Object { [int]$_ }) + @($next | ForEach-Object { [int]$_ }) | Sort-Object -Unique
    $igloo = @()
    foreach ($index in $indexes) {
        $name = 'Boot{0:X4}' -f $index
        $raw = [IglooEvidenceEfi]::Read($name)
        if (-not $raw) { continue }
        $formatted = Format-LoadOption $raw
        $lines.Add("$name $formatted")
        if ($formatted -match "^'[^']*(?i:igloo)") { $igloo += $name }
    }
    $script:facts.Add(('uefi: BootOrder {0}; BootNext {1}; BootCurrent {2}; iGloo entries: {3}' -f (($order | ForEach-Object { $_.ToString('X4') }) -join ','),
        $(if ($next.Count) { $next[0].ToString('X4') } else { 'not set' }), $(if ($current.Count) { $current[0].ToString('X4') } else { '?' }), $(if ($igloo) { $igloo -join ',' } else { 'none' })))
    Save-Text 'uefi-variables.txt' ($lines -join "`r`n")
}

if ($Phase -eq 'prepared') {
    Invoke-Section 'boot registration check' {
        $check = Join-Path $PSScriptRoot 'Check-BootRegistration.ps1'
        if (-not (Test-Path -LiteralPath $check)) { throw "Check-BootRegistration.ps1 is not next to this script." }
        $text = Invoke-Native (Get-Process -Id $PID).Path @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $check)
        Save-Text 'check-boot-registration.txt' $text
        $script:facts.Add('Check-BootRegistration.ps1: ' + (($text -split "`r?`n" | Where-Object { $_ -match 'failure\(s\)' } | Select-Object -Last 1)))
    }
}

# ---- iGloo logs and journals ------------------------------------------------------------
Invoke-Section 'igloo logs' {
    $logs = Join-Path $IglooData 'logs'
    if (-not (Test-Path -LiteralPath $logs)) { $script:facts.Add("wizard log: $logs does not exist"); return }
    $files = @(Get-ChildItem -LiteralPath $logs -File | Sort-Object LastWriteTime)
    foreach ($file in $files) { Save-Text (Join-Path 'logs' $file.Name) (Read-SharedText $file.FullName $maxLogBytes) }
    $newest = $files | Select-Object -Last 1
    if ($newest) {
        $text = Read-SharedText $newest.FullName $maxLogBytes
        $errors = @($text -split "`r?`n" | Where-Object { $_ -match '\[(ERR|FTL)\]' })
        $script:facts.Add(('wizard log: {0} file(s); newest {1} ({2:N0} bytes) with {3} error line(s)' -f $files.Count, $newest.Name, $newest.Length, $errors.Count))
        Save-Text 'wizard-log-errors.txt' ($errors -join "`r`n")
    }
}

Invoke-Section 'igloo journals' {
    foreach ($name in 'operations', 'plans', 'targets', 'allocations', 'artifacts') {
        $dir = Join-Path $IglooData $name
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $dir -File -Filter *.json | Select-Object -First 2000)) {
            Save-Text (Join-Path (Join-Path 'journals' $name) $file.Name) (Read-SharedText $file.FullName 4MB)
        }
    }
    if (Test-Path -LiteralPath $IglooData) { Save-Text 'igloo-data-listing.txt' (Format-Tree (Get-Tree $IglooData)) }
}

# ---- Source data -------------------------------------------------------------------------
Invoke-Section 'test data' {
    if (-not (Test-Path -LiteralPath $TestDataPath)) { $script:facts.Add("test data: $TestDataPath does not exist"); return }
    $tree = @(Get-Tree $TestDataPath)
    $lines = foreach ($item in $tree) {
        $relative = $item.Path.Replace('\', '/')
        switch ($item.Kind) {
            'D' { "D $relative/" }
            'F' { 'F {0} {1} {2}' -f (Get-Sha256 $item.Full), $item.Size, $relative }
            default { 'X {0} {1}' -f $item.Kind, $relative }
        }
    }
    $sorted = [string[]]@($lines)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    Save-Text 'testdata-manifest.txt' (($sorted -join "`n") + "`n")
    $files = @($tree | Where-Object { $_.Kind -eq 'F' })
    $script:facts.Add(('test data: {0} files, {1} directories, {2:N0} bytes in {3}' -f $files.Count, @($tree | Where-Object { $_.Kind -eq 'D' }).Count, ($files | Measure-Object Size -Sum).Sum, $TestDataPath))
}

Invoke-Section 'browsers' {
    $lines = New-Object System.Collections.Generic.List[string]
    $counts = foreach ($name in 'msedge', 'chrome', 'firefox') {
        $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        '{0}={1} ({2} with a window)' -f $name, $processes.Count, @($processes | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }).Count
    }
    $lines.Add('running: ' + ($counts -join '; '))
    $script:facts.Add('browsers running: ' + ($counts -join '; '))
    $roots = [ordered]@{
        'Microsoft Edge' = (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data')
        'Google Chrome'  = (Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data')
        'Firefox'        = (Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles')
    }
    $wanted = 'Local State', 'Login Data', 'Bookmarks', 'History', 'Preferences', 'Network\Cookies', 'logins.json', 'key4.db', 'places.sqlite', 'cookies.sqlite'
    foreach ($browser in $roots.Keys) {
        $root = $roots[$browser]
        if (-not (Test-Path -LiteralPath $root)) { $lines.Add("$browser`: no profile root at $root"); continue }
        $lines.Add("$browser`: $root")
        $profiles = @($root) + @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $browser -eq 'Firefox' -or $_.Name -eq 'Default' -or $_.Name -like 'Profile *' } | ForEach-Object { $_.FullName })
        foreach ($profile in $profiles) {
            foreach ($relative in $wanted) {
                $info = New-Object IO.FileInfo((Join-Path $profile $relative))
                if ($info.Exists) { $lines.Add(('  {0,12}  {1:yyyy-MM-dd HH:mm:ss}  {2}' -f $info.Length, $info.LastWriteTime, $info.FullName.Substring($root.Length).TrimStart('\'))) }
            }
        }
    }
    Save-Text 'browsers.txt' ($lines -join "`r`n")
}

Invoke-Section 'wifi' {
    Save-Text 'wifi.txt' ((Invoke-Native 'netsh.exe' @('wlan', 'show', 'interfaces')) + "`r`n" + (Invoke-Native 'netsh.exe' @('wlan', 'show', 'profiles')))
}

# ---- Summary -----------------------------------------------------------------------------
try { [IO.Directory]::Delete($tmp, $true) } catch { }
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("iGloo evidence, Windows side. run=$Run phase=$Phase")
$summary.AddRange($facts)
$summary.Add('')
if ($problems.Count) { $summary.Add("Sections with a problem ($($problems.Count)):"); $summary.AddRange($problems) } else { $summary.Add('Every section completed.') }
Save-Text 'summary.txt' ($summary -join "`r`n")
Write-Host ''
$summary | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Evidence written to $out"
