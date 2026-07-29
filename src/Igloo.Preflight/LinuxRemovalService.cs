using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public sealed class LinuxRemovalService : ILinuxRemovalService
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    private readonly ILogger<LinuxRemovalService> _logger;

    public LinuxRemovalService(ILogger<LinuxRemovalService> logger) => _logger = logger;

    public Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Remove(installations, seedLeftovers, removingAllLinux, progress, ct), ct);

    private void Remove(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken ct)
    {
        var failures = 0;

        foreach (var install in installations)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Removing {install.DisplayName} from {install.DiskModel}…");

            // Delete back-to-front so partition numbers stay valid even on
            // providers that renumber after a deletion.
            foreach (var p in install.Partitions.OrderByDescending(p => p.OffsetBytes))
            {
                if (!DeletePartition(install.DiskNumber, p.Index))
                    failures++;
            }

            if (install.FirmwareEntryIndex is { } index)
                EfiBootEntries.Delete(index, _logger);
        }

        foreach (var leftover in seedLeftovers)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Removing iGloo installer partition {leftover.Partition.Label}…");
            if (!DeletePartition(leftover.DiskNumber, leftover.Partition.Index))
                failures++;
        }

        // Boot-menu hygiene: iGloo's one-shot entries are always ours to delete;
        // every Linux-classified entry goes only when no Linux remains to boot.
        foreach (var entry in EfiBootEntries.Enumerate(_logger))
        {
            if (EfiBootEntries.IsIglooDescription(entry.Description) ||
                (removingAllLinux && EfiBootEntries.IsLinuxDescription(entry.Description)))
                EfiBootEntries.Delete(entry.Index, _logger);
        }

        // ESP hygiene  the "mountvol S: /S && rmdir \EFI\<distro>" step people
        // otherwise do by hand. Whitelisted Linux loader folders only, and only
        // when no Linux remains that could still need them.
        if (removingAllLinux)
        {
            progress?.Report("Cleaning Linux boot files from the EFI partition…");
            CleanEfiSystemPartitions();

            // A distribution that built its OWN ESP instead of reusing Windows' leaves
            // that partition behind once its root is gone: an empty second EFI
            // partition that still advertises itself to the firmware. Beyond the wasted
            // space it confuses the next install's boot handoff, so it goes too - under
            // the strict rules in the method below.
            progress?.Report("Removing the leftover EFI partition…");
            foreach (var disk in installations.Select(i => i.DiskNumber).Distinct())
                RemoveRedundantEfiPartitions(disk);
        }

        // Give the space back - and do it LAST, once every partition this removal is
        // going to delete is actually gone.
        //
        // Ordering is load-bearing, not stylistic. A volume can only grow into ADJACENT
        // trailing free space, so a single surviving partition between Windows and the
        // freed region caps how far C: can extend. Reclaiming before the leftover ESP
        // was deleted did exactly that: the ESP sat between Windows and the Linux root,
        // C: grew by the 4 GB in front of it, and ~50 GB behind it stayed unallocated -
        // leaving the user to finish the job in Disk Management, which is precisely the
        // kind of "now go fix it yourself" iGloo exists to avoid.
        if (failures == 0)
        {
            foreach (var disk in installations.Select(i => i.DiskNumber)
                         .Concat(seedLeftovers.Select(s => s.DiskNumber))
                         .Distinct())
            {
                ct.ThrowIfCancellationRequested();
                TryReclaimFreedSpace(disk, progress);
            }
        }

        // Symmetry with DirectInstallService.SetRtcUniversalTime(): with the last
        // Linux gone, nothing needs the RTC in UTC anymore  restore Windows'
        // stock local-time behavior so the machine is left exactly as found.
        if (removingAllLinux)
            RestoreRtcLocalTime();

        if (failures > 0)
            throw new InvalidOperationException(
                $"{failures} partition(s) could not be deleted. Re-run the system check " +
                "to see the current state; a reboot may release in-use volumes.");

        progress?.Report("Removal complete.");
    }

    //   Reclaim freed space                          

    private long TryReclaimFreedSpace(uint diskNumber, IProgress<string>? progress)
    {
        long reclaimed = 0;
        const long MiB = 1024L * 1024;
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results = searcher.Get();

            // Same heuristic as the shrink service: the largest lettered
            // partition is the disk's main data partition.
            ManagementObject? best = null;
            long bestSize = 0;
            char bestLetter = '\0';
            foreach (var p in results.Cast<ManagementObject>())
            {
                var dl = WmiValues.ToDriveLetter(p["DriveLetter"]);
                var size = dl == '\0' ? 0 : Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture);
                if (size > bestSize)
                {
                    best?.Dispose();
                    (best, bestSize, bestLetter) = (p, size, dl);
                }
                else
                {
                    p.Dispose();
                }
            }
            if (best is null)
                return reclaimed;

            using (best)
            {
                var supported = best.InvokeMethod("GetSupportedSize",
                    best.GetMethodParameters("GetSupportedSize"), null)!;
                if (Convert.ToUInt32(supported["ReturnValue"], CultureInfo.InvariantCulture) != 0)
                    return reclaimed;

                var sizeMax = Convert.ToInt64(supported["SizeMax"], CultureInfo.InvariantCulture);
                var gainBytes = sizeMax - bestSize;
                if (gainBytes < 64 * MiB)
                    return reclaimed;   // no adjacent space to grow into

                var gainGb = gainBytes / (1024.0 * MiB);
                progress?.Report($"Adding {gainGb:N1} GB back to {bestLetter}:…");

                var inParams = best.GetMethodParameters("Resize");
                inParams["Size"] = (ulong)sizeMax;
                var result = best.InvokeMethod("Resize", inParams, null)!;
                var returnValue = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
                if (returnValue != 0)
                    _logger.LogWarning(
                        "Resize({Letter}:) to reclaim freed space returned {Code} (non-fatal) - " +
                        "the space stays unallocated", bestLetter, returnValue);
                else
                {
                    reclaimed = gainBytes;
                    _logger.LogInformation("Extended {Letter}: on disk {Disk} by {Gb:N1} GB",
                        bestLetter, diskNumber, gainGb);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex,
                "Could not reclaim freed space on disk {Disk} (non-fatal)", diskNumber);
        }
        return reclaimed;
    }

    public Task<long> ReclaimFreeSpaceAsync(uint diskNumber,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => TryReclaimFreedSpace(diskNumber, progress), ct);

    //   EFI System Partition cleanup                     ─

    private static readonly string[] LinuxEfiFolders =
    [
        "ubuntu", "fedora", "debian", "linuxmint", "opensuse", "suse", "manjaro",
        "arch", "endeavouros", "garuda", "cachyos", "nobara", "zorin",
        "elementary", "neon", "kylin", "openkylin", "deepin", "uos", "bazzite",
        "mx", "mxlinux", "grub", "systemd", "centos", "rocky", "alma", "tuxedo",
        "solus", "gentoo", "slackware", "void", "nixos",
    ];

    private static readonly string[] LinuxEfiFolderPrefixes = ["pop_os", "pop!_os"];

    private void CleanEfiSystemPartitions()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                "SELECT AccessPaths FROM MSFT_Partition " +
                "WHERE GptType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'");
            using var results = searcher.Get();

            foreach (var esp in results.Cast<ManagementBaseObject>())
            {
                var volumePath = (esp["AccessPaths"] as string[])?
                    .FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
                if (volumePath is null)
                    continue;

                CleanOneEsp(volumePath);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException
                                   or InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "ESP cleanup failed (non-fatal) - Linux boot " +
                "folders may remain on the EFI partition");
        }
    }

    /// <summary>
    /// Deletes an EFI System Partition left behind by a distribution that created its
    /// own instead of reusing Windows'.
    /// </summary>
    /// <remarks>
    /// This is the most destructive operation in the service: removing the ESP that
    /// Windows boots from leaves an unbootable machine. Every rule below exists to make
    /// that impossible, and all of them must hold before anything is deleted:
    ///
    ///   1. The disk must have MORE THAN ONE ESP. A single ESP is always load-bearing.
    ///   2. Exactly one ESP must be identified as Windows' - it carries
    ///      \EFI\Microsoft\Boot\bootmgfw.efi, or the storage stack flags it IsSystem.
    ///      Without a positive identification we delete NOTHING; "probably redundant"
    ///      is not good enough.
    ///   3. The candidate must be neither of those things, and must contain no
    ///      remaining OS loader (the Linux folders were already removed above, so
    ///      anything still there belongs to something we do not know about).
    ///
    /// Size is deliberately NOT a criterion. "Bigger than the usual 99 MB" looks like a
    /// signal but is not: OEMs ship 300-500 MB Windows ESPs, and a rule based on size
    /// would eventually delete somebody's boot partition.
    /// </remarks>
    private void RemoveRedundantEfiPartitions(uint diskNumber)
    {
        try
        {
            var esps = QueryEfiPartitions(diskNumber);
            if (esps.Count < 2)
            {
                _logger.LogInformation(
                    "Disk {Disk}: {Count} EFI partition(s) - nothing to remove (a lone ESP is never touched)",
                    diskNumber, esps.Count);
                return;
            }

            var windowsEsps = esps.Where(e => e.IsSystem || HasWindowsBootManager(e.VolumePath)).ToList();
            if (windowsEsps.Count == 0)
            {
                _logger.LogWarning(
                    "Disk {Disk}: {Count} EFI partitions but none positively identified as Windows' - " +
                    "removing none of them", diskNumber, esps.Count);
                return;
            }

            foreach (var esp in esps)
            {
                if (windowsEsps.Any(w => w.PartitionNumber == esp.PartitionNumber))
                    continue;

                if (HasRemainingLoader(esp.VolumePath))
                {
                    _logger.LogWarning(
                        "EFI partition {Disk}:{Part} still contains a boot loader - leaving it alone",
                        diskNumber, esp.PartitionNumber);
                    continue;
                }

                if (DeletePartition(diskNumber, esp.PartitionNumber))
                    _logger.LogInformation(
                        "Deleted the leftover EFI partition {Disk}:{Part} ({MiB} MiB); Windows' ESP on " +
                        "partition {WinPart} is untouched",
                        diskNumber, esp.PartitionNumber, esp.SizeBytes / (1024 * 1024),
                        windowsEsps[0].PartitionNumber);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException
                                   or InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Leftover-EFI-partition cleanup failed (non-fatal)");
        }
    }

    private readonly record struct EspInfo(int PartitionNumber, string VolumePath, bool IsSystem, long SizeBytes);

    private static List<EspInfo> QueryEfiPartitions(uint diskNumber)
    {
        var result = new List<EspInfo>();
        using var searcher = new ManagementObjectSearcher(StorageNs,
            "SELECT PartitionNumber, AccessPaths, IsSystem, Size FROM MSFT_Partition " +
            $"WHERE DiskNumber = {diskNumber} AND GptType = '{{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}}'");
        using var results = searcher.Get();

        foreach (var p in results.Cast<ManagementBaseObject>())
        {
            var volumePath = (p["AccessPaths"] as string[])?
                .FirstOrDefault(a => a.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
            if (volumePath is null)
                continue;
            result.Add(new EspInfo(
                Convert.ToInt32(p["PartitionNumber"], CultureInfo.InvariantCulture),
                volumePath,
                p["IsSystem"] is bool isSystem && isSystem,
                Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static bool HasWindowsBootManager(string volumePath)
    {
        try
        {
            return File.Exists(Path.Combine(volumePath, "EFI", "Microsoft", "Boot", "bootmgfw.efi"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Unreadable means unprovable, and an ESP we cannot inspect is one we treat
            // as Windows' - the safe direction for this particular question.
            return true;
        }
    }

    /// <summary>True when anything that looks like an OS loader remains on the ESP.</summary>
    private static bool HasRemainingLoader(string volumePath)
    {
        try
        {
            var efiRoot = Path.Combine(volumePath, "EFI");
            if (!Directory.Exists(efiRoot))
                return false;

            // \EFI\BOOT holds the removable fallback loader, which is not evidence of an
            // installed OS; anything ELSE with content is.
            return Directory.EnumerateDirectories(efiRoot)
                .Any(dir => !string.Equals(Path.GetFileName(dir), "BOOT", StringComparison.OrdinalIgnoreCase)
                            && Directory.EnumerateFileSystemEntries(dir).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;   // cannot inspect - assume occupied and keep it
        }
    }

    private void CleanOneEsp(string volumePath)
    {
        var efiRoot = Path.Combine(volumePath, "EFI");
        if (Directory.Exists(efiRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(efiRoot))
            {
                var name = Path.GetFileName(dir);
                var isLinux = LinuxEfiFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || LinuxEfiFolderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (!isLinux)
                    continue;

                TryDeleteEspFolder(dir);
            }
        }

        // systemd-boot keeps its menu config at the ESP root, outside \EFI.
        var loaderDir = Path.Combine(volumePath, "loader");
        if (Directory.Exists(loaderDir))
            TryDeleteEspFolder(loaderDir);
    }

    private void TryDeleteEspFolder(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted Linux boot folder {Dir} from the ESP", dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Could not delete {Dir} (non-fatal)", dir);
        }
    }

    private void RestoreRtcLocalTime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", writable: true);
            if (key?.GetValue("RealTimeIsUniversal") is not null)
            {
                key.DeleteValue("RealTimeIsUniversal");
                _logger.LogInformation("RealTimeIsUniversal removed - Windows RTC back to local time");
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not remove RealTimeIsUniversal (non-fatal)");
        }
    }

    private bool DeletePartition(uint diskNumber, int partitionNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} " +
                $"AND PartitionNumber = {partitionNumber}");
            using var results = searcher.Get();
            var partition = results.Cast<ManagementObject>().FirstOrDefault();
            if (partition is null)
            {
                _logger.LogWarning("Partition {Disk}:{Part} no longer exists - skipped",
                    diskNumber, partitionNumber);
                return true;
            }

            using (partition)
            {
                var result = partition.InvokeMethod("DeleteObject", partition.GetMethodParameters("DeleteObject"), null)!;
                var returnValue = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
                if (returnValue != 0)
                {
                    _logger.LogError("MSFT_Partition.DeleteObject({Disk}:{Part}) returned {Code}",
                        diskNumber, partitionNumber, returnValue);
                    return false;
                }
            }

            _logger.LogInformation("Deleted partition {Disk}:{Part}", diskNumber, partitionNumber);
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogError(ex, "DeletePartition({Disk}:{Part}) failed", diskNumber, partitionNumber);
            return false;
        }
    }
}
