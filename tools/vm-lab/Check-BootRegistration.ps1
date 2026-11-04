<#
.SYNOPSIS
  Verifies, inside the Windows guest, that iGloo's one-shot installer boot entry was
  registered. Run as Administrator AFTER the wizard shows "Ready to reboot" and BEFORE
  the restart (or cancel the 10-second countdown first).

.DESCRIPTION
  Beta Track B-04. Checks three independent things and exits non-zero if any fails:
    1. UEFI NVRAM: BootNext exists and names a Boot#### whose description contains
       "igloo" OR that targets the installer partition (firmware-created sibling, the
       Gigabyte/AMI case). BootOrder lists that entry.
    2. BCD: exactly one firmware application described "iGloo distribution installer"
       and {fwbootmgr} bootsequence points at it. Zero or more than one is a failure
       (stale-entry cleanup did not run, or /copy ran twice).
    3. Disk: exactly one 6 GiB-class FAT32 data partition on the Windows disk (B-01
       leftover detection). A warning, not a failure, until B-01 is implemented.
  Optionally greps the iGloo log for the expected lines.

.PARAMETER LogPath
  Path to the Community wizard log (Serilog). Default: newest *.log under
  %LOCALAPPDATA%\Igloo\logs if that folder exists.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Check-BootRegistration.ps1
#>
[CmdletBinding()]
param(
    [string]$LogPath,
    [string]$Description = 'iGloo distribution installer'
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
function Fail($m) { $script:failures.Add($m); Write-Host "FAIL  $m" -ForegroundColor Red }
function Warn($m) { $script:warnings.Add($m); Write-Host "WARN  $m" -ForegroundColor Yellow }
function Pass($m) { Write-Host "PASS  $m" -ForegroundColor Green }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator (UEFI variable reads need SeSystemEnvironmentPrivilege).'
}

# ---- UEFI NVRAM access -------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Efi {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFirmwareEnvironmentVariableW(string name, string guid, byte[] buffer, uint size);
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
    public const string Guid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
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
        var n = GetFirmwareEnvironmentVariableW(name, Guid, buf, (uint)buf.Length);
        if (n == 0) return null;
        var r = new byte[n]; Array.Copy(buf, r, n); return r;
    }
}
'@

$priv = [Efi]::EnablePrivilege()
if ($priv -ne 0) { Warn "SeSystemEnvironmentPrivilege enable returned Win32 error $priv (1300 = not held; NVRAM reads may fail)" }

function Get-U16s([byte[]]$b) { if (-not $b) { return @() }; $o = @(); for ($i = 0; $i + 1 -lt $b.Length; $i += 2) { $o += [BitConverter]::ToUInt16($b, $i) }; return $o }
function Get-LoadOptionDescription([byte[]]$b) {
    if (-not $b -or $b.Length -lt 6) { return $null }
    # EFI_LOAD_OPTION: UINT32 Attributes, UINT16 FilePathListLength, CHAR16 Description[] (NUL-terminated)
    $end = 6; while ($end + 1 -lt $b.Length -and -not ($b[$end] -eq 0 -and $b[$end + 1] -eq 0)) { $end += 2 }
    return [Text.Encoding]::Unicode.GetString($b, 6, $end - 6)
}

$bootNext  = Get-U16s ([Efi]::Read('BootNext'))
$bootOrder = Get-U16s ([Efi]::Read('BootOrder'))
Write-Host ("BootOrder: " + (($bootOrder | ForEach-Object { $_.ToString('X4') }) -join ','))

if ($bootNext.Count -ne 1) {
    Fail "BootNext is not set (expected one Boot#### index). The NVRAM path did not complete."
} else {
    $idx = $bootNext[0]
    $raw = [Efi]::Read(('Boot{0:X4}' -f $idx))
    if (-not $raw) {
        Fail ("BootNext names Boot{0:X4} but that variable does not exist (pruned by firmware?)" -f $idx)
    } else {
        $desc = Get-LoadOptionDescription $raw
        Write-Host ("BootNext -> Boot{0:X4} '{1}' ({2} bytes)" -f $idx, $desc, $raw.Length)
        if ($desc -match 'igloo') {
            Pass ("BootNext -> Boot{0:X4} is the iGloo entry" -f $idx)
        } else {
            Warn ("BootNext -> Boot{0:X4} '{1}' is not described as iGloo; acceptable only if it is the firmware's own entry for the installer partition" -f $idx, $desc)
        }
        if ($bootOrder -contains $idx) { Pass "BootOrder contains the BootNext entry" } else { Fail "BootOrder does not contain the BootNext entry" }
        if ($bootOrder.Count -gt 0 -and $bootOrder[0] -eq $idx) { Pass "BootNext entry is first in BootOrder" } else { Warn "BootNext entry is not first in BootOrder (only matters on firmware that ignores BootNext)" }
    }
}

# Every Boot#### described iGloo
$iglooEntries = @()
for ($i = 0; $i -lt 0x100; $i++) {
    $r = [Efi]::Read(('Boot{0:X4}' -f $i)); if (-not $r) { continue }
    $d = Get-LoadOptionDescription $r
    if ($d -match 'igloo') { $iglooEntries += ('Boot{0:X4}' -f $i) }
}
Write-Host ("iGloo NVRAM entries: " + ($(if ($iglooEntries) { $iglooEntries -join ',' } else { 'none' })))
if ($iglooEntries.Count -gt 1) { Warn "More than one iGloo Boot#### exists; earlier runs were not cleaned (the first-boot agent removes them, but leftovers waste slots)" }

# ---- BCD -------------------------------------------------------------------------
$enum = & bcdedit /enum firmware 2>&1 | Out-String
$blocks = $enum -split "(\r?\n){2,}"
$bcdIds = @()
foreach ($b in $blocks) {
    # Description test first: the LAST -match decides what $Matches holds.
    if ($b -match [regex]::Escape($Description) -and $b -match "(?m)^identifier\s+(\{[0-9a-fA-F-]{36}\})") { $bcdIds += $Matches[1] }
}
Write-Host ("BCD '$Description' objects: " + ($(if ($bcdIds) { $bcdIds -join ',' } else { 'none' })))
switch ($bcdIds.Count) {
    1 { Pass "Exactly one iGloo BCD firmware application" }
    0 { Fail "No iGloo BCD firmware application (bcdedit /copy did not run or failed; NVRAM path alone must carry the boot)" }
    default { Fail "$($bcdIds.Count) iGloo BCD objects; stale-entry cleanup did not run" }
}
$fw = & bcdedit /enum '{fwbootmgr}' 2>&1 | Out-String
if ($fw -match "(?m)^bootsequence\s+(\{[0-9a-fA-F-]{36}\})") {
    if ($bcdIds -contains $Matches[1]) { Pass "{fwbootmgr} bootsequence -> iGloo object $($Matches[1])" } else { Fail "{fwbootmgr} bootsequence -> $($Matches[1]) which is not the iGloo object" }
} else {
    Fail "{fwbootmgr} has no bootsequence"
}

# ---- RTC -------------------------------------------------------------------------
$rtc = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation' -Name RealTimeIsUniversal -ErrorAction SilentlyContinue).RealTimeIsUniversal
if ($rtc -eq 1) { Pass "RealTimeIsUniversal=1" } else { Warn "RealTimeIsUniversal is not 1 (non-fatal clock drift after dual boot)" }

# ---- Disk leftovers (B-01) ---------------------------------------------------------
try {
    $sys = Get-Partition | Where-Object { $_.DriveLetter -eq $env:SystemDrive.TrimEnd(':') } | Select-Object -First 1
    $fat = Get-Partition -DiskNumber $sys.DiskNumber | Where-Object { $_.Type -eq 'Basic' -and $_.Size -gt 5GB -and $_.Size -lt 8GB }
    $fatVols = @(); foreach ($p in $fat) { $v = $p | Get-Volume -ErrorAction SilentlyContinue; if ($v -and $v.FileSystem -eq 'FAT32') { $fatVols += ('partition {0} {1}: {2:N0} bytes' -f $p.PartitionNumber, $v.DriveLetter, $p.Size) } }
    Write-Host ("FAT32 installer-class partitions on disk $($sys.DiskNumber): " + ($(if ($fatVols) { $fatVols -join '; ' } else { 'none' })))
    if ($fatVols.Count -eq 1) { Pass "One installer partition" } elseif ($fatVols.Count -eq 0) { Fail "No installer (OEMDRV) partition found" } else { Warn "Multiple installer-class partitions; earlier runs left partitions behind (Beta Track B-01)" }
} catch { Warn "Partition check skipped: $($_.Exception.Message)" }

# ---- Log -------------------------------------------------------------------------
if (-not $LogPath) {
    $dir = Join-Path $env:LOCALAPPDATA 'Igloo\logs'
    if (Test-Path $dir) { $LogPath = (Get-ChildItem $dir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
}
if ($LogPath -and (Test-Path $LogPath)) {
    $log = Get-Content $LogPath -Raw
    foreach ($needle in 'Writing UEFI Boot', 'BootNext set to', 'BCD firmware entry', 'BootNext + BootOrder updated') {
        if ($log -match [regex]::Escape($needle)) { Pass "log: '$needle'" } else { Fail "log lacks '$needle' ($LogPath)" }
    }
    if ($log -match 'CommunityRecoveryBoundaryException|Boot-registration planning blocked') { Fail "log still contains the recovery-gate refusal; the running binary is not the repaired build" }
} else { Warn "No iGloo log found; pass -LogPath to check the expected lines" }

Write-Host ''
Write-Host ("{0} failure(s), {1} warning(s)" -f $failures.Count, $warnings.Count)
if ($failures.Count -gt 0) { exit 1 } else { exit 0 }
