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
        var (linuxInstalls, seedLeftovers) = BuildLinuxInventory(disks);

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
            LinuxInstallations = linuxInstalls,
            SeedLeftovers = seedLeftovers,
        };
    }

    // ── Linux inventory ──────────────────────────────────────────────────────

    /// <summary>GPT type GUIDs that mark a partition as belonging to a Linux install.</summary>
    private static readonly HashSet<string> LinuxGptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "{0fc63daf-8483-4772-8e79-3d69d8477de4}", // Linux filesystem
        "{0657fd6d-a4ab-43c4-84e5-0933c84b4f4f}", // Linux swap
        "{e6d6d379-f507-44c2-a23c-238f2a3df928}", // Linux LVM
        "{933ac7e1-2eb4-4f13-b844-0e14e2aef915}", // /home
        "{bc13c2ff-59e6-4262-a352-b275fd6f7172}", // extended boot (XBOOTLDR)
        "{ca7d7ccb-63ed-4c53-861c-1742536059cc}", // LUKS
        "{4f68bce3-e8cd-4db1-96e7-fbcaf984b709}", // root, x86-64 (discoverable-partitions spec)
    };

    private static readonly string[] SeedLabels = ["OEMDRV", "CIDATA", "IGLOOISO"];

    /// <summary>
    /// Groups each disk's contiguous run of Linux-typed partitions into one
    /// installation and collects leftover iGloo seed partitions. Names come from
    /// the machine's UEFI boot entries when they can be paired unambiguously:
    /// with exactly one install (or equal counts, paired in order) the loader's
    /// own description ("ubuntu", "Fedora") is used; otherwise a generic name.
    /// </summary>
    private (IReadOnlyList<LinuxInstallation>, IReadOnlyList<SeedLeftover>)
        BuildLinuxInventory(IReadOnlyList<DiskInfo> disks)
    {
        var groups = new List<(uint DiskNumber, string Model, List<PartitionInfo> Parts)>();
        var leftovers = new List<SeedLeftover>();

        foreach (var disk in disks)
        {
            if (!TryParseDiskNumber(disk.DeviceId, out var diskNumber))
                continue;

            List<PartitionInfo>? run = null;
            foreach (var p in disk.Partitions.OrderBy(p => p.OffsetBytes))
            {
                if (SeedLabels.Contains(p.Label ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    leftovers.Add(new SeedLeftover(diskNumber, disk.Model, p));
                    continue;   // a seed partition between Linux partitions doesn't split the run
                }

                if (p.GptType is not null && LinuxGptTypes.Contains(p.GptType))
                {
                    if (run is null)
                    {
                        run = [];
                        groups.Add((diskNumber, disk.Model, run));
                    }
                    run.Add(p);
                }
                else
                {
                    run = null; // non-Linux partition ends the contiguous run
                }
            }
        }

        if (groups.Count == 0)
            return ([], leftovers);

        // Best-effort naming from UEFI boot entries (empty list on any failure).
        var linuxEntries = EfiBootEntries.Enumerate(_logger)
            .Where(e => EfiBootEntries.IsLinuxDescription(e.Description))
            .ToList();

        var installs = new List<LinuxInstallation>();
        for (var i = 0; i < groups.Count; i++)
        {
            var (diskNumber, model, parts) = groups[i];
            string name;
            ushort? entryIndex = null;

            if (groups.Count == linuxEntries.Count && linuxEntries.Count > 0)
            {
                name = Prettify(linuxEntries[i].Description);
                entryIndex = linuxEntries[i].Index;
            }
            else if (groups.Count == 1 && linuxEntries.Count > 0)
            {
                name = Prettify(linuxEntries[0].Description);
                entryIndex = linuxEntries.Count == 1 ? linuxEntries[0].Index : null;
            }
            else
            {
                name = "Linux installation";
            }

            installs.Add(new LinuxInstallation(
                name, diskNumber, model, parts, parts.Sum(p => p.SizeBytes), entryIndex));
        }

        return (installs, leftovers);
    }

    private static bool TryParseDiskNumber(string deviceId, out uint number)
    {
        const string marker = "PHYSICALDRIVE";
        var at = deviceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        number = 0;
        return at >= 0 && uint.TryParse(deviceId[(at + marker.Length)..], out number);
    }

    /// <summary>"ubuntu" → "Ubuntu"; already-capitalized descriptions pass through.</summary>
    private static string Prettify(string description) =>
        description.Length > 0 && char.IsLower(description[0])
            ? char.ToUpperInvariant(description[0]) + description[1..]
            : description;

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
        // Attempt 1 - Win32_Tpm is the authoritative source but requires elevation
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
            _logger.LogDebug(ex, "Win32_Tpm query inaccessible - trying PnP fallback");
        }

        // Attempt 2 - Win32_PnPEntity is readable without elevation.
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
            if (obj == null)
                return BitLockerState.Unknown;

            // ConversionStatus 0 = FullyDecrypted, 1 = FullyEncrypted, 2+ = in transition.
            // ProtectionStatus 0 = Off (protection suspended or no key protector), 1 = On.
            var conversion = Convert.ToUInt32(obj["ConversionStatus"]);
            var protection = Convert.ToUInt32(obj["ProtectionStatus"]);

            // ConversionStatus values from Win32_EncryptableVolume:
            //   0 = FullyDecrypted  1 = FullyEncrypted  2 = EncryptionInProgress
            //   3 = DecryptionInProgress  4 = EncryptionPaused  5 = DecryptionPaused
            if (conversion == 0)
                return BitLockerState.NotEncrypted;
            if (conversion == 3 || conversion == 5)
                return BitLockerState.DecryptionInProgress;
            if (protection == 1)
                return BitLockerState.EncryptedAndUnlocked;
            if (protection == 0)
                return BitLockerState.SuspendedProtection;
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
                $"SELECT PartitionNumber, Size, Offset, GptType, IsSystem, IsBoot, DriveLetter " +
                $"FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results = searcher.Get();
            foreach (ManagementBaseObject p in results)
            {
                var index = Convert.ToInt32(p["PartitionNumber"]);
                var size = Convert.ToInt64(p["Size"]);
                var offset = p["Offset"] is null ? -1L : Convert.ToInt64(p["Offset"]);
                var gptType = (p["GptType"] as string)?.Trim();
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
                var shrinkable = fs == "NTFS" ? QueryShrinkableBytes(diskNumber, (uint)index, p) : 0L;
                partitions.Add(new PartitionInfo(index, fs, size, label, isSystem, isBoot, shrinkable,
                                                 offset, gptType));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MSFT_Partition query failed for disk {DiskNumber}", diskNumber);
        }
        // Disk order, not enumeration order: WMI returns partitions in arbitrary sequence.
        return partitions.OrderBy(p => p.OffsetBytes >= 0 ? p.OffsetBytes : long.MaxValue).ToList();
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
            if (mo is null)
                return 0;

            var outParams = mo.InvokeMethod("GetSupportedSize", null, null);
            if (outParams is null)
                return 0;

            var returnValue = Convert.ToUInt32(outParams["ReturnValue"]);
            if (returnValue != 0)
                return 0;   // 0 = success

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
        if (driveLetter == '\0')
            return ("Unknown", null);
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
