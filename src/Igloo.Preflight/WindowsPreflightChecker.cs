using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
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

    //   Linux inventory                            

    
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

        // A UEFI boot entry maps to a partition group with certainty only when there is
        // exactly one Linux install and one Linux boot entry. BootOrder is unrelated to
        // partition (disk-offset) order, and a machine that has installed/removed distros
        // before carries stale entries - so pairing groups to entries by list index
        // mislabels installs and, worse, would delete the wrong boot entry on removal.
        // When the match is ambiguous every group gets a generic name and no entry; the
        // removal path's boot-menu hygiene still clears iGloo's own entries and (when all
        // Linux is going) every Linux entry, so nothing is left orphaned.
        var espDistros = DetectEspDistros(groups[0].DiskNumber);

        // A contiguous run can hold more than one distro installed back to back, so expand each
        // run into its per-distro partition sub-groups: one unit per individually-removable install.
        var units = new List<(uint DiskNumber, string Model, List<PartitionInfo> Parts)>();
        foreach (var (diskNumber, model, parts) in groups)
            foreach (var sub in SplitRunByDistro(parts))
                units.Add((diskNumber, model, sub));

        // Attribute several installs to specific distros only by a reliable signal, most trustworthy
        // first: ESP grub.cfg ↔ ext4 superblock UUID (tells same-family installs apart), then the
        // LVM-vs-plain GPT type (RHEL family vs Debian family). Neither → stay generic below.
        List<(List<PartitionInfo> Parts, string Name)>? attributed = null;
        if (units.Count > 1)
        {
            if (TryAttributeByEsp(groups[0].DiskNumber, units, out var byEsp))
                attributed = byEsp;
            else if (TryAttributeDistros([.. units.Select(u => u.Parts)], espDistros, out var byLvm))
                attributed = byLvm;
        }

        var installs = new List<LinuxInstallation>();

        if (units.Count == 1)
        {
            // One install can be named with confidence: the sole ESP loader folder, else its
            // sole matching boot entry (see ResolveInstallIdentity for the stale-entry guard).
            var (diskNumber, model, parts) = units[0];
            var (fallbackName, fallbackEntry) = ResolveInstallIdentity(1, linuxEntries);
            var singleName = espDistros.Count == 1 ? espDistros[0] : fallbackName;
            var singleEntry = espDistros.Count == 1 ? EntryIndexFor(espDistros[0], linuxEntries) : fallbackEntry;
            installs.Add(new LinuxInstallation(
                singleName, diskNumber, model, parts, parts.Sum(p => p.SizeBytes), singleEntry));
        }
        else if (attributed is not null)
        {
            foreach (var (subParts, distroName) in attributed)
            {
                var unit = units.First(u => ReferenceEquals(u.Parts, subParts));
                installs.Add(new LinuxInstallation(
                    distroName, unit.DiskNumber, unit.Model, subParts,
                    subParts.Sum(p => p.SizeBytes), EntryIndexFor(distroName, linuxEntries)));
            }
        }
        else
        {
            // Several installs Windows cannot pin to a specific distro. Naming them by guess could
            // delete the wrong OS, so each stays a plainly-named, individually-sized entry - the size
            // and disk shown in the UI let the user pick - and no boot entry is attached.
            foreach (var (diskNumber, model, parts) in units)
                installs.Add(new LinuxInstallation(
                    "Linux installation", diskNumber, model, parts, parts.Sum(p => p.SizeBytes), null));
        }

        return (installs, leftovers);
    }

    private const string LinuxLvmGptType = "{e6d6d379-f507-44c2-a23c-238f2a3df928}";

    // Installers that put the root filesystem inside LVM by default. This is the one
    // structural signal Windows can read (the GPT type) that separates the RHEL/Fedora
    // family from the Debian/Ubuntu family, which use a plain partition.
    private static readonly HashSet<string> LvmFamilyDistros = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fedora", "Nobara",
    };

    internal static List<List<PartitionInfo>> SplitRunByDistro(IReadOnlyList<PartitionInfo> run)
    {
        const long RootMinBytes = 4L * 1024 * 1024 * 1024;
        var groups = new List<List<PartitionInfo>>();
        var pending = new List<PartitionInfo>();

        foreach (var part in run)
        {
            pending.Add(part);
            if (part.SizeBytes >= RootMinBytes)
            {
                groups.Add(pending);
                pending = [];
            }
        }

        if (pending.Count > 0 && groups.Count > 0)
            groups[^1].AddRange(pending);
        else if (pending.Count > 0)
            groups.Add(pending);

        return groups;
    }

    internal static bool TryAttributeDistros(
        List<List<PartitionInfo>> groups, IReadOnlyList<string> espDistros,
        out List<(List<PartitionInfo> Parts, string Name)> attributed)
    {
        attributed = [];
        if (groups.Count != 2 || espDistros.Count != 2)
            return false;

        var lvmFamily = espDistros.Where(LvmFamilyDistros.Contains).ToList();
        var otherFamily = espDistros.Where(d => !LvmFamilyDistros.Contains(d)).ToList();
        if (lvmFamily.Count != 1 || otherFamily.Count != 1)
            return false;

        var lvmGroups = groups.Where(g => g.Any(IsLvm)).ToList();
        var plainGroups = groups.Where(g => !g.Any(IsLvm)).ToList();
        if (lvmGroups.Count != 1 || plainGroups.Count != 1)
            return false;

        attributed.Add((lvmGroups[0], lvmFamily[0]));
        attributed.Add((plainGroups[0], otherFamily[0]));
        return true;
    }

    private static bool IsLvm(PartitionInfo p) =>
        string.Equals(p.GptType, LinuxLvmGptType, StringComparison.OrdinalIgnoreCase);

    private static ushort? EntryIndexFor(string distroName, IReadOnlyList<EfiBootEntries.BootEntry> linuxEntries)
    {
        var matches = linuxEntries
            .Where(e => string.Equals(Prettify(e.Description), distroName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].Index : null;
    }

    private static readonly Dictionary<string, string> EfiFolderDistroNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fedora"] = "Fedora",
        ["ubuntu"] = "Ubuntu",
        ["debian"] = "Debian",
        ["linuxmint"] = "Linux Mint",
        ["opensuse"] = "openSUSE",
        ["manjaro"] = "Manjaro",
        ["zorin"] = "Zorin OS",
        ["nobara"] = "Nobara",
        ["garuda"] = "Garuda",
        ["neon"] = "KDE neon",
    };

    private List<string> DetectEspDistros(uint diskNumber)
    {
        var distros = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber} " +
                "AND GptType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'");
            using var results = searcher.Get();

            foreach (var esp in results.Cast<ManagementBaseObject>())
            {
                var volumePath = (esp["AccessPaths"] as string[])?
                    .FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
                if (volumePath is null)
                    continue;

                var efiRoot = Path.Combine(volumePath, "EFI");
                if (!Directory.Exists(efiRoot))
                    continue;

                foreach (var dir in Directory.EnumerateDirectories(efiRoot))
                {
                    if (EfiFolderDistroNames.TryGetValue(Path.GetFileName(dir), out var display)
                        && !distros.Contains(display))
                        distros.Add(display);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException
                                   or InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not read ESP loader folders on disk {Disk}", diskNumber);
        }
        return distros;
    }

    /// <summary>
    /// Attributes each partition group to a distro by matching the root filesystem UUID in that
    /// distro's ESP grub.cfg to the UUID in a partition's ext4 superblock. This is the reliable
    /// way to tell two same-family installs apart (e.g. Ubuntu vs Debian). Returns false - so the
    /// caller falls back to a safe generic name - unless every group matches a distinct distro.
    /// </summary>
    private bool TryAttributeByEsp(
        uint diskNumber,
        List<(uint DiskNumber, string Model, List<PartitionInfo> Parts)> units,
        out List<(List<PartitionInfo> Parts, string Name)> attributed)
    {
        attributed = [];

        var grubUuids = ReadEspGrubUuids(diskNumber);   // distro display name -> root fs UUID
        if (grubUuids.Count < units.Count)
            return false;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            string? matched = null;
            foreach (var part in unit.Parts)
            {
                var partUuid = ReadPartitionExt4Uuid(unit.DiskNumber, part);
                if (partUuid is null)
                    continue;
                foreach (var (distro, grubUuid) in grubUuids)
                    if (!used.Contains(distro) && LinuxVolumeId.UuidsMatch(partUuid, grubUuid))
                    {
                        matched = distro;
                        break;
                    }
                if (matched is not null)
                    break;
            }

            if (matched is null || !used.Add(matched))
                return false;   // an unmatched group → don't guess; let the caller stay generic
            attributed.Add((unit.Parts, matched));
        }

        return true;
    }

    /// <summary>Root filesystem UUID declared by each ESP <c>\EFI\&lt;distro&gt;\grub.cfg</c>, keyed by display name.</summary>
    private Dictionary<string, string> ReadEspGrubUuids(uint diskNumber)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber} " +
                "AND GptType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'");
            using var results = searcher.Get();

            foreach (var esp in results.Cast<ManagementBaseObject>())
            {
                var volumePath = (esp["AccessPaths"] as string[])?
                    .FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
                var efiRoot = volumePath is null ? null : Path.Combine(volumePath, "EFI");
                if (efiRoot is null || !Directory.Exists(efiRoot))
                    continue;

                foreach (var dir in Directory.EnumerateDirectories(efiRoot))
                {
                    if (!EfiFolderDistroNames.TryGetValue(Path.GetFileName(dir), out var display)
                        || map.ContainsKey(display))
                        continue;
                    var grubCfg = Path.Combine(dir, "grub.cfg");
                    if (File.Exists(grubCfg) &&
                        LinuxVolumeId.ParseGrubRootFsUuid(File.ReadAllText(grubCfg)) is { } uuid)
                        map[display] = uuid;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException
                                   or InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not read ESP grub configs on disk {Disk}", diskNumber);
        }
        return map;
    }

    /// <summary>
    /// The ext4 filesystem UUID of a partition, read straight from its superblock, or null when the
    /// read fails or the partition is not ext-family. Read-only raw access; failures degrade to null
    /// so attribution simply falls back to a generic name rather than erroring.
    /// </summary>
    private string? ReadPartitionExt4Uuid(uint diskNumber, PartitionInfo part)
    {
        if (part.OffsetBytes < 0)
            return null;
        try
        {
            using var handle = File.OpenHandle(
                $@"\\.\PHYSICALDRIVE{diskNumber}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[4096];   // sector-aligned read covering the ext4 superblock UUID
            var read = RandomAccess.Read(handle, buffer, part.OffsetBytes);
            return LinuxVolumeId.ReadExt4Uuid(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Could not read partition {Part} superblock on disk {Disk}", part.Index, diskNumber);
            return null;
        }
    }

    internal static (string name, ushort? entryIndex) ResolveInstallIdentity(
        int groupCount, IReadOnlyList<EfiBootEntries.BootEntry> linuxEntries)
    {
        if (groupCount == 1 && linuxEntries.Count == 1)
            return (Prettify(linuxEntries[0].Description), linuxEntries[0].Index);
        return ("Linux installation", null);
    }

    private static bool TryParseDiskNumber(string deviceId, out uint number)
    {
        const string marker = "PHYSICALDRIVE";
        var at = deviceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        number = 0;
        return at >= 0 && uint.TryParse(deviceId[(at + marker.Length)..], out number);
    }

    
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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
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
            var conversion = Convert.ToUInt32(obj["ConversionStatus"], CultureInfo.InvariantCulture);
            var protection = Convert.ToUInt32(obj["ProtectionStatus"], CultureInfo.InvariantCulture);

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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "BitLocker WMI query failed");
            return BitLockerState.Unknown;
        }
    }

    private List<DiskInfo> QueryDisks()
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
                var number = Convert.ToUInt32(disk["Number"], CultureInfo.InvariantCulture);
                var model = (string?)disk["FriendlyName"] ?? "Unknown";
                var total = Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture);
                var allocated = Convert.ToInt64(disk["AllocatedSize"], CultureInfo.InvariantCulture);
                // PartitionStyle: 0=Unknown, 1=MBR, 2=GPT
                var style = Convert.ToInt32(disk["PartitionStyle"], CultureInfo.InvariantCulture) switch
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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "MSFT_Disk query failed");
        }
        return disks;
    }

    private List<PartitionInfo> QueryPartitionsForDisk(uint diskNumber)
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
                var index = Convert.ToInt32(p["PartitionNumber"], CultureInfo.InvariantCulture);
                var size = Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture);
                var offset = p["Offset"] is null ? -1L : Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture);
                var gptType = (p["GptType"] as string)?.Trim();
                var isSystem = p["IsSystem"] is bool bs && bs;
                var isBoot = p["IsBoot"] is bool bb && bb;

                char dl = WmiValues.ToDriveLetter(p["DriveLetter"]);

                var (fs, label) = QueryVolumeInfo(dl);
                var shrinkable = fs == "NTFS" ? QueryShrinkableBytes(diskNumber, (uint)index, p) : 0L;
                partitions.Add(new PartitionInfo(index, fs, size, label, isSystem, isBoot, shrinkable,
                                                 offset, gptType));
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "MSFT_Partition query failed for disk {DiskNumber}", diskNumber);
        }
        // Disk order, not enumeration order: WMI returns partitions in arbitrary sequence.
        return partitions.OrderBy(p => p.OffsetBytes >= 0 ? p.OffsetBytes : long.MaxValue).ToList();
    }

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

            var returnValue = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);
            if (returnValue != 0)
                return 0;   // 0 = success

            var sizeMin = Convert.ToInt64(outParams["SizeMin"], CultureInfo.InvariantCulture);
            var sizeMax = Convert.ToInt64(outParams["SizeMax"], CultureInfo.InvariantCulture);
            return Math.Max(0, sizeMax - sizeMin);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
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
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
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
            return cs == null ? 0 : Convert.ToInt64(cs["TotalPhysicalMemory"], CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "RAM WMI query failed");
            return 0;
        }
    }

    private static List<PreflightFinding> BuildFindings(
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
