using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public sealed class LinuxRemovalService : ILinuxRemovalService
{
    private readonly IWindowsStorageReader _storage;

    private readonly ILogger<LinuxRemovalService> _logger;

    public LinuxRemovalService(ILogger<LinuxRemovalService> logger) : this(logger, new WindowsStorageReader()) { }
    public LinuxRemovalService(ILogger<LinuxRemovalService> logger, IWindowsStorageReader storage)
    { _logger = logger; _storage = storage; }

    public Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        HostProtection.RequireWritable("Linux removal");
        return Task.Run(() => Remove(installations, seedLeftovers, removingAllLinux, progress, ct), ct);
    }

    private void Remove(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken ct)
    {
        // Everything this removal touches is decided on one layout, read before the first delete.
        WindowsStorageRow[] layout;
        try
        {
            layout = [.. _storage.ReadPartitions().RowsOrThrow()];
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("The partition layout could not be read. Nothing was removed.", ex);
        }

        var ownedSeeds = new List<(uint Disk, WindowsStorageRow Row)>();
        foreach (var leftover in seedLeftovers)
        {
            if (FindOwnedSeed(leftover, layout) is { } owned)
            {
                ownedSeeds.Add((leftover.DiskNumber, owned));
                continue;
            }
            _logger.LogWarning("Partition {Disk}:{Part} ({Label}) is not provably an iGloo installer partition - left in place",
                leftover.DiskNumber, leftover.Partition.Index, leftover.Partition.Label);
            progress?.Report($"Left {leftover.Partition.Label} in place: it is not an iGloo installer partition.");
        }

        var plan = LinuxRemovalPlanner.Plan(installations, ownedSeeds, removingAllLinux, layout,
            EfiBootEntries.EnumerateTargets(_logger));
        var failures = plan.UnmatchedPartitions;
        if (plan.UnmatchedPartitions > 0)
            _logger.LogWarning("{Count} reported Linux partition(s) are no longer where System Check found them - not deleted",
                plan.UnmatchedPartitions);

        foreach (var (disk, row, description) in plan.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Removing {description}…");
            if (!DeleteObserved(row, disk))
                failures++;
        }

        foreach (var index in plan.BootEntries)
            EfiBootEntries.Delete(index, _logger);

        if (plan.BootCleanupDisks.Count > 0)
        {
            progress?.Report("Cleaning Linux boot files from the EFI partition…");
            CleanEfiSystemPartitions(plan.BootCleanupDisks);

            // A distribution that built its own ESP leaves it behind once its root is gone.
            progress?.Report("Removing the leftover EFI partition…");
            foreach (var disk in plan.BootCleanupDisks)
                RemoveRedundantEfiPartitions(disk);
        }
        else if (plan.SkippedBootCleanup is { } reason)
        {
            _logger.LogWarning("Linux boot files and firmware entries are left in place: {Reason}", reason);
            progress?.Report($"Left the Linux boot files in place: {reason}.");
        }

        // Last, once every partition is gone: a volume only grows into adjacent free space.
        if (failures == 0)
        {
            foreach (var disk in plan.Partitions.Select(deleted => deleted.Disk).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                TryReclaimFreedSpace(disk, progress);
            }
        }

        // Windows goes back to a local-time clock only when no Linux can be left to need UTC.
        if (plan.BootCleanupDisks.Count > 0)
        {
            RestoreRtcLocalTime();
            FastStartup.RestoreAfterLinux(_logger);
        }

        if (failures > 0)
            throw new InvalidOperationException(
                $"{failures} partition(s) could not be deleted. Re-run the system check " +
                "to see the current state; a reboot may release in-use volumes.");

        progress?.Report("Removal complete.");
    }

    //   Reclaim freed space                          

    internal const long MinimumGainBytes = 64L * 1024 * 1024;

    /// <summary>Unallocated bytes that directly follow a partition, read from the partition table.</summary>
    internal static long AdjacentFreeBytes(IEnumerable<WindowsStorageRow> partitions, long offset, long size, long diskBytes)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        const long MiB = 1024L * 1024;
        var end = offset + size;
        // Without a later partition the space runs to the last MiB, which holds the backup table.
        var next = partitions.Select(row => Convert.ToInt64(row["Offset"], CultureInfo.InvariantCulture))
            .Where(begin => begin >= end).DefaultIfEmpty(diskBytes - MiB).Min();
        return Math.Max(0, (next - end) / MiB * MiB);
    }

    /// <summary>What a volume extension did; <see cref="LeftBytes"/> is adjacent space Windows did not add.</summary>
    private readonly record struct Reclaim(long ReclaimedBytes, long LeftBytes, char Letter, uint Code)
    {
        public string? Notice => LeftBytes < MinimumGainBytes ? null : FormattableString.Invariant(
            $"{LeftBytes / (1024.0 * 1024 * 1024):N1} GB directly behind {Letter}: is unallocated: Windows did not extend {Letter}: (storage code {Code}). ") +
            FormattableString.Invariant($"Check {Letter}: for errors with 'chkdsk {Letter}: /f' in an administrator terminal, ") +
            "restart Windows and run the system check again.";
    }

    private Reclaim TryReclaimFreedSpace(uint diskNumber, IProgress<string>? progress)
    {
        try
        {
            // Same heuristic as the shrink service: the largest lettered
            // partition is the disk's main data partition.
            var rows = _storage.ReadPartitions((int)diskNumber).RowsOrThrow().ToArray();
            WindowsStorageRow? best = null;
            long bestSize = 0;
            char bestLetter = '\0';
            foreach (var p in rows)
            {
                var dl = WmiValues.ToDriveLetter(p["DriveLetter"]);
                var size = dl == '\0' ? 0 : Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture);
                if (size > bestSize)
                {
                    (best, bestSize, bestLetter) = (p, size, dl);
                }
            }
            if (best is null)
                return default;

            var diskBytes = _storage.ReadDisks().RowsOrThrow()
                .Where(disk => Convert.ToUInt32(disk["Number"], CultureInfo.InvariantCulture) == diskNumber)
                .Select(disk => Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture)).FirstOrDefault();
            var tableGain = diskBytes == 0 ? 0 : AdjacentFreeBytes(rows,
                Convert.ToInt64(best["Offset"], CultureInfo.InvariantCulture), bestSize, diskBytes);

            using var target = new ManagementObject(best.ObjectPath);
            var sizeCode = uint.MaxValue;
            long offered = 0;
            // Right after a deletion Windows can still answer for the old layout.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var answer = WindowsStorageReader.ReadSupportedSize(target, explicitParameters: true);
                sizeCode = answer.Value is { } supported
                    ? Convert.ToUInt32(supported["ReturnValue"], CultureInfo.InvariantCulture) : uint.MaxValue;
                offered = sizeCode == 0
                    ? Convert.ToInt64(answer.Value!["SizeMax"], CultureInfo.InvariantCulture) - bestSize : 0;
                if (answer.Error is not null)
                    _logger.LogWarning(answer.Error, "Windows could not say how large {Letter}: may become", bestLetter);
                if (offered >= MinimumGainBytes || tableGain < MinimumGainBytes)
                    break;
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            if (offered < MinimumGainBytes && tableGain < MinimumGainBytes)
                return default;   // no adjacent space to grow into

            var gainBytes = offered >= MinimumGainBytes ? offered : tableGain;
            if (offered < MinimumGainBytes)
                _logger.LogWarning(
                    "Windows did not offer the {Gb:N1} GB behind {Letter}: (supported-size code {Code}); " +
                    "asking for the size the partition table allows",
                    tableGain / (1024.0 * 1024 * 1024), bestLetter, sizeCode);

            var gainGb = gainBytes / (1024.0 * 1024 * 1024);
            progress?.Report($"Adding {gainGb:N1} GB back to {bestLetter}:…");

            HostProtection.RequireWritable("Windows volume extension");
            var inParams = target.GetMethodParameters("Resize");
            inParams["Size"] = (ulong)(bestSize + gainBytes);
            var result = target.InvokeMethod("Resize", inParams, null)!;
            var returnValue = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            if (returnValue != 0)
            {
                var left = new Reclaim(0, gainBytes, bestLetter, returnValue);
                _logger.LogWarning(
                    "Resize({Letter}:) by {Gb:N1} GB returned {Code} (supported-size code {SizeCode}) - the space stays unallocated",
                    bestLetter, gainGb, returnValue, sizeCode);
                progress?.Report(left.Notice!);
                return left;
            }

            _logger.LogInformation("Extended {Letter}: on disk {Disk} by {Gb:N1} GB",
                bestLetter, diskNumber, gainGb);
            return new Reclaim(gainBytes, 0, bestLetter, 0);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex,
                "Could not reclaim freed space on disk {Disk} (non-fatal)", diskNumber);
            return default;
        }
    }

    public Task<long> ReclaimFreeSpaceAsync(uint diskNumber,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        HostProtection.RequireWritable("Windows volume extension");
        return Task.Run(() =>
        {
            var reclaim = TryReclaimFreedSpace(diskNumber, progress);
            // The page shows this sentence; the space behind the volume is still there to see.
            return reclaim.Notice is { } notice ? throw new FreeSpaceNotReclaimedException(notice) : reclaim.ReclaimedBytes;
        }, ct);
    }

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

    // Only on the disks an installation was removed from; another disk's loaders are not this removal's to judge.
    private void CleanEfiSystemPartitions(IReadOnlyCollection<uint> disks)
    {
        try
        {
            foreach (var esp in _storage.ReadPartitions(efiOnly: true).RowsOrThrow())
            {
                if (!disks.Contains(Convert.ToUInt32(esp["DiskNumber"], CultureInfo.InvariantCulture)))
                    continue;
                var volumePath = (esp["AccessPaths"] as string[])?
                    .FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
                if (volumePath is null)
                    continue;

                CleanOneEsp(volumePath);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException or FormatException or OverflowException
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

            foreach (var esp in esps
                         .Where(e => !windowsEsps.Any(w => w.PartitionNumber == e.PartitionNumber)))
            {
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

    private List<EspInfo> QueryEfiPartitions(uint diskNumber)
    {
        var result = new List<EspInfo>();
        foreach (var p in _storage.ReadPartitions((int)diskNumber, efiOnly: true).RowsOrThrow())
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
            return File.Exists(Path.Join(volumePath, "EFI", "Microsoft", "Boot", "bootmgfw.efi"));
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
            var efiRoot = Path.Join(volumePath, "EFI");
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
        var efiRoot = Path.Join(volumePath, "EFI");
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
        var loaderDir = Path.Join(volumePath, "loader");
        if (Directory.Exists(loaderDir))
            TryDeleteEspFolder(loaderDir);
    }

    private void TryDeleteEspFolder(string dir)
    {
        try
        {
            HostProtection.RequireWritable("EFI system partition cleanup");
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
        HostProtection.RequireWritable("RealTimeIsUniversal registry value");
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

    internal Func<string, bool> SeedMarkerProbe { get; init; } = DirectInstallService.IsIglooSeedVolume;

    // The live partition behind a reported seed leftover, or null when ownership is not proven.
    internal WindowsStorageRow? FindOwnedSeed(SeedLeftover leftover, IReadOnlyList<WindowsStorageRow>? layout = null)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        try
        {
            var onDisk = layout?.Where(row => Convert.ToUInt32(row["DiskNumber"], CultureInfo.InvariantCulture) == leftover.DiskNumber)
                ?? _storage.ReadPartitions((int)leftover.DiskNumber).RowsOrThrow();
            return InstallerSeedOwnership.IsSeedLabel(leftover.Partition.Label)
                ? InstallerSeedOwnership.FindOwnedSeed(onDisk,
                    leftover.Partition.OffsetBytes, leftover.Partition.SizeBytes, SeedMarkerProbe)
                : null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Ownership of partition {Disk}:{Part} could not be read", leftover.DiskNumber, leftover.Partition.Index);
            return null;
        }
    }

    private bool DeletePartition(uint diskNumber, int partitionNumber)
    {
        try
        {
            var observed = _storage.ReadPartitions((int)diskNumber, partitionNumber).RowsOrThrow().FirstOrDefault();
            if (observed is null)
            {
                _logger.LogWarning("Partition {Disk}:{Part} no longer exists - skipped",
                    diskNumber, partitionNumber);
                return true;
            }
            return DeleteObserved(observed, diskNumber);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogError(ex, "DeletePartition({Disk}:{Part}) failed", diskNumber, partitionNumber);
            return false;
        }
    }

    private bool DeleteObserved(WindowsStorageRow observed, uint diskNumber)
    {
        var partitionNumber = -1;
        try
        {
            partitionNumber = Convert.ToInt32(observed["PartitionNumber"], CultureInfo.InvariantCulture);
            using var partition = new ManagementObject(observed.ObjectPath);
            HostProtection.RequireWritable("partition deletion");
            var result = partition.InvokeMethod("DeleteObject", partition.GetMethodParameters("DeleteObject"), null)!;
            var returnValue = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            if (returnValue != 0)
            {
                _logger.LogError("MSFT_Partition.DeleteObject({Disk}:{Part}) returned {Code}",
                    diskNumber, partitionNumber, returnValue);
                return false;
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
