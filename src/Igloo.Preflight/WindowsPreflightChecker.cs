using System.Management;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsPreflightChecker : IPreflightChecker
{
    private readonly ILogger<WindowsPreflightChecker> _logger;

    public WindowsPreflightChecker(ILogger<WindowsPreflightChecker> logger) => _logger = logger;

    public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
        Task.Run(() => Collect(ct), ct);

    private PreflightReport Collect(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isUefi = QueryIsUefi();
        var secureBoot = QuerySecureBoot();
        var tpmPresent = QueryTpmPresent();
        var bitLocker = QueryBitLockerState();
        var disks = QueryDisks();
        var gpuVendor = QueryGpuVendor();
        var totalRam = QueryTotalRam();
        var findings = BuildFindings(isUefi, secureBoot, tpmPresent, bitLocker, totalRam);

        return new PreflightReport
        {
            IsUefi = isUefi,
            SecureBootEnabled = secureBoot,
            TpmPresent = tpmPresent,
            BitLocker = bitLocker,
            Disks = disks,
            GpuVendor = gpuVendor,
            TotalRamBytes = totalRam,
            Findings = findings,
        };
    }

    // Presence of this registry key is the reliable UEFI indicator on Windows 10+.
    private static bool QueryIsUefi()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key != null;
    }

    private static bool QuerySecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int v && v == 1;
    }

    private bool QueryTpmPresent()
    {
        // Attempt 1 — Win32_Tpm is the authoritative source but requires elevation
        // on most Windows 11 configurations.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftTpm",
                "SELECT IsEnabled_InitialValue FROM Win32_Tpm");
            using var results = searcher.Get();
            if (results.Cast<ManagementBaseObject>().Any())
                return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Win32_Tpm query inaccessible — trying PnP fallback");
        }

        // Attempt 2 — Win32_PnPEntity is readable without elevation.
        // ClassGuid {d94ee5d8-d189-4994-83d2-f68d7d41b0e4} is the Windows
        // "Security Devices" class; TPM chips (1.2 and 2.0) always register here.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity " +
                "WHERE ClassGuid = '{d94ee5d8-d189-4994-83d2-f68d7d41b0e4}'");
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>().Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TPM PnP fallback query failed");
            return false;
        }
    }

    private BitLockerState QueryBitLockerState()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT ConversionStatus, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'");
            using var results = searcher.Get();
            var obj = results.Cast<ManagementBaseObject>().FirstOrDefault();
            if (obj == null) return BitLockerState.Unknown;

            // ConversionStatus 0 = FullyDecrypted, 1 = FullyEncrypted, 2+ = in transition.
            // ProtectionStatus 0 = Off (protection suspended or no key protector), 1 = On.
            var conversion = Convert.ToUInt32(obj["ConversionStatus"]);
            var protection = Convert.ToUInt32(obj["ProtectionStatus"]);

            // ConversionStatus values from Win32_EncryptableVolume:
            //   0 = FullyDecrypted  1 = FullyEncrypted  2 = EncryptionInProgress
            //   3 = DecryptionInProgress  4 = EncryptionPaused  5 = DecryptionPaused
            if (conversion == 0) return BitLockerState.NotEncrypted;
            if (conversion == 3 || conversion == 5) return BitLockerState.DecryptionInProgress;
            if (protection == 1) return BitLockerState.EncryptedAndUnlocked;
            if (protection == 0) return BitLockerState.SuspendedProtection;
            return BitLockerState.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BitLocker WMI query failed");
            return BitLockerState.Unknown;
        }
    }

    private IReadOnlyList<DiskInfo> QueryDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Number, FriendlyName, Size, AllocatedSize, PartitionStyle FROM MSFT_Disk");
            using var results = searcher.Get();
            foreach (ManagementBaseObject disk in results)
            {
                var number = Convert.ToUInt32(disk["Number"]);
                var model = (string?)disk["FriendlyName"] ?? "Unknown";
                var total = Convert.ToInt64(disk["Size"]);
                var allocated = Convert.ToInt64(disk["AllocatedSize"]);
                // PartitionStyle: 0=Unknown, 1=MBR, 2=GPT
                var style = Convert.ToInt32(disk["PartitionStyle"]) switch
                {
                    1 => "MBR",
                    2 => "GPT",
                    _ => "Unknown",
                };
                var partitions = QueryPartitionsForDisk(number);
                disks.Add(new DiskInfo(
                    $"\\\\.\\PHYSICALDRIVE{number}", model, total, total - allocated, style, partitions));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MSFT_Disk query failed");
        }
        return disks;
    }

    private IReadOnlyList<PartitionInfo> QueryPartitionsForDisk(uint diskNumber)
    {
        var partitions = new List<PartitionInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT PartitionNumber, Size, IsSystem, IsBoot, DriveLetter " +
                $"FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results = searcher.Get();
            foreach (ManagementBaseObject p in results)
            {
                var index = Convert.ToInt32(p["PartitionNumber"]);
                var size = Convert.ToInt64(p["Size"]);
                var isSystem = p["IsSystem"] is bool bs && bs;
                var isBoot = p["IsBoot"] is bool bb && bb;

                // DriveLetter is a WMI Char16; providers may return it as char, ushort, or string.
                char dl = p["DriveLetter"] switch
                {
                    char c => c,
                    ushort u when u > 0 => (char)u,
                    string s when s.Length > 0 => s[0],
                    _ => '\0',
                };

                var (fs, label) = QueryVolumeInfo(dl);
                var shrinkable  = fs == "NTFS" ? QueryShrinkableBytes(diskNumber, (uint)index, p) : 0L;
                partitions.Add(new PartitionInfo(index, fs, size, label, isSystem, isBoot, shrinkable));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MSFT_Partition query failed for disk {DiskNumber}", diskNumber);
        }
        return partitions;
    }

    /// <summary>
    /// Calls <c>MSFT_Partition.GetSupportedSize()</c> to find how many bytes the partition
    /// can be shrunk.  Returns 0 on any failure (non-NTFS, locked, etc.).
    /// </summary>
    private long QueryShrinkableBytes(uint diskNumber, uint partitionNumber, ManagementBaseObject partObj)
    {
        try
        {
            // Re-query as ManagementObject so we can invoke methods on it.
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} " +
                $"AND PartitionNumber = {partitionNumber}");
            using var results = searcher.Get();
            var mo = results.Cast<ManagementObject>().FirstOrDefault();
            if (mo is null) return 0;

            var outParams = mo.InvokeMethod("GetSupportedSize", null, null);
            if (outParams is null) return 0;

            var returnValue = Convert.ToUInt32(outParams["ReturnValue"]);
            if (returnValue != 0) return 0;   // 0 = success

            var sizeMin = Convert.ToInt64(outParams["SizeMin"]);
            var sizeMax = Convert.ToInt64(outParams["SizeMax"]);
            return Math.Max(0, sizeMax - sizeMin);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetSupportedSize failed for disk {Disk} partition {Part}",
                diskNumber, partitionNumber);
            return 0;
        }
    }

    private (string FileSystem, string? Label) QueryVolumeInfo(char driveLetter)
    {
        if (driveLetter == '\0') return ("Unknown", null);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT FileSystem, Label FROM Win32_Volume WHERE DriveLetter = '{driveLetter}:'");
            using var results = searcher.Get();
            var vol = results.Cast<ManagementBaseObject>().FirstOrDefault();
            return ((string?)vol?["FileSystem"] ?? "Unknown", (string?)vol?["Label"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Win32_Volume query failed for drive {DriveLetter}", driveLetter);
            return ("Unknown", null);
        }
    }

    private string QueryGpuVendor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AdapterCompatibility FROM Win32_VideoController");
            using var results = searcher.Get();
            var gpu = results.Cast<ManagementBaseObject>().FirstOrDefault();
            return (string?)gpu?["AdapterCompatibility"] ?? "Unknown";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU WMI query failed");
            return "Unknown";
        }
    }

    private long QueryTotalRam()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            var cs = results.Cast<ManagementBaseObject>().FirstOrDefault();
            return cs == null ? 0 : Convert.ToInt64(cs["TotalPhysicalMemory"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAM WMI query failed");
            return 0;
        }
    }

    private static IReadOnlyList<PreflightFinding> BuildFindings(
        bool isUefi, bool secureBoot, bool tpmPresent, BitLockerState bitLocker, long totalRam)
    {
        var findings = new List<PreflightFinding>();

        if (!isUefi)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "BIOS_LEGACY",
                "Machine uses Legacy BIOS. Most Linux installers require UEFI.",
                "Enable UEFI mode in firmware settings."));

        if (secureBoot)
            findings.Add(new PreflightFinding(FindingSeverity.Info, "SECURE_BOOT_ON",
                "Secure Boot is enabled. Distributions that don't support Secure Boot will be greyed out in the next step.",
                null));

        if (bitLocker is BitLockerState.EncryptedAndUnlocked or BitLockerState.SuspendedProtection)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "BITLOCKER_ACTIVE",
                "BitLocker encryption is active on the system drive. The partition table cannot be modified while encrypted.",
                "Disable BitLocker in Windows Settings › Privacy & Security › Device Encryption before proceeding."));

        if (bitLocker is BitLockerState.DecryptionInProgress)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "BITLOCKER_DECRYPTING",
                "BitLocker decryption is in progress. Wait for it to finish before proceeding.",
                "You can monitor progress in Windows Settings › Privacy & Security › Device Encryption."));

        if (!tpmPresent)
            findings.Add(new PreflightFinding(FindingSeverity.Info, "NO_TPM",
                "No TPM chip detected.", null));

        const long minRamBytes = 2L * 1024 * 1024 * 1024;
        if (totalRam > 0 && totalRam < minRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "LOW_RAM",
                $"Only {totalRam / (1024 * 1024)} MB of RAM detected. Most Linux distributions require at least 2 GB.",
                "Consider a lightweight distribution such as Debian netinstall or Alpine Linux."));

        return findings;
    }
}
