using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

// Refuses a layout the installer could misread, before the first write (Beta Track B-02).
public sealed partial class DirectInstallService
{
    private const uint FileBackedVirtualBus = 15;
    private const string RemoveEarlierLinux =
        " Windows has not been resized. Remove the earlier Linux installation in System Check, then start again.";

    internal readonly record struct LayoutPartition(long Offset, long Size, bool IsWindows, bool IsEsp);

    internal readonly record struct FreeRegion(long Offset, long Size);

    /// <param name="ExpectedLinuxBytes">What the installer will find in the region once the installer partitions exist.</param>
    internal sealed record LinuxRegionPlan(bool ShrinksWindows, FreeRegion Region, long LargestFreeBytes,
        long ExpectedLinuxBytes);

    // Unallocated gaps as an installer sees them; no alignment margin is taken off.
    internal static List<FreeRegion> FreeRegions(IEnumerable<(long Offset, long Size)> partitions, long diskSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var regions = new List<FreeRegion>();
        if (diskSizeBytes <= 0) return regions;
        long cursor = 0;
        foreach (var (offset, size) in partitions.Where(p => p.Size > 0).OrderBy(p => p.Offset))
        {
            if (offset > cursor)
                regions.Add(new(cursor, offset - cursor));
            cursor = Math.Max(cursor, offset + size);
        }
        if (diskSizeBytes > cursor)
            regions.Add(new(cursor, diskSizeBytes - cursor));
        return regions;
    }

    /// <summary>The one unallocated region the installer will be given once the plan has run, or a refusal.</summary>
    /// <param name="plannedBytes">Linux allocation plus the installer partitions.</param>
    /// <param name="installerBytes">The installer partitions alone; zero when an existing one is reused.</param>
    internal static LinuxRegionPlan PlanLinuxRegion(int diskNumber, IReadOnlyList<LayoutPartition> partitions,
        long diskSizeBytes, long plannedBytes, long installerBytes, bool reusesInstallerPartition,
        InstallerTargetRules rules)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(rules);
        if (diskSizeBytes <= 0 || plannedBytes <= 0 || installerBytes < 0 || installerBytes > plannedBytes ||
            rules.MinimumRegionBytes <= 0)
            throw new InvalidOperationException(
                $"The partition layout of disk {diskNumber} could not be evaluated. Windows has not been resized.");
        var minimumGiB = rules.MinimumRegionBytes / (1024.0 * 1024 * 1024);

        var espCount = partitions.Count(p => p.IsEsp);
        if (rules.UsesExistingEsp && espCount > 1)
            throw new InstallationRefusedException(
                $"Disk {diskNumber} has {espCount} EFI system partitions and this installer would choose one of them itself." +
                RemoveEarlierLinux);

        var layout = partitions.Select(p => (p.Offset, p.Size)).ToList();
        var largest = LargestFreeExtentBytes(layout, diskSizeBytes);
        var shrinks = !reusesInstallerPartition && largest < plannedBytes;
        if (shrinks)
        {
            var windows = partitions.Where(p => p.IsWindows).ToArray();
            if (windows.Length != 1 || windows[0].Size <= plannedBytes)
                throw new InstallationRefusedException(FormattableString.Invariant(
                    $"Disk {diskNumber} has no unallocated region of {plannedBytes / (1024.0 * 1024 * 1024):N1} GiB and no single Windows partition that can be shrunk to make one. Windows has not been resized."));
            layout = partitions.Select(p => p.IsWindows ? (p.Offset, p.Size - plannedBytes) : (p.Offset, p.Size)).ToList();
        }

        var eligible = FreeRegions(layout, diskSizeBytes).Where(region => region.Size >= rules.MinimumRegionBytes).ToArray();
        if (eligible.Length == 0)
            throw new InstallationRefusedException(FormattableString.Invariant(
                $"Disk {diskNumber} has no unallocated region of {minimumGiB:N0} GiB or more left for Linux.") + RemoveEarlierLinux);
        if (eligible.Length > 1)
            throw new InstallationRefusedException(FormattableString.Invariant(
                $"Disk {diskNumber} would have {eligible.Length} unallocated regions of {minimumGiB:N0} GiB or more, and the installer must be given exactly one.") +
                RemoveEarlierLinux);
        var expected = reusesInstallerPartition ? eligible[0].Size : eligible[0].Size - installerBytes;
        return new(shrinks, eligible[0], largest, expected);
    }

    /// <param name="InstallerOffset">Where the installer partition goes.</param>
    /// <param name="LinuxBytes">The unallocated space left directly behind it, which is all the installer partitions.</param>
    internal readonly record struct InstallerPlacement(long InstallerOffset, long LinuxBytes);

    /// <param name="ShrinkBytes">What Windows gives up; zero when unallocated space that is already there holds the plan.</param>
    internal sealed record BoundLinuxPlan(long ShrinkBytes, InstallerPlacement Placement)
    {
        public bool ShrinksWindows => ShrinkBytes > 0;
    }

    // What the installer may find behind the installer partition beyond the size asked: alignment and the end of the disk.
    internal const long LinuxExtentSlackBytes = 64 * MiB;

    // GPT keeps its backup table in the last sectors, so a partition never reaches the end of the disk.
    private static long UsableEnd(long end, long diskSizeBytes) =>
        (end >= diskSizeBytes ? diskSizeBytes - MiB : end) / MiB * MiB;

    /// <summary>Puts the installer partition at the end of a region so that the Linux size, and no more, is left behind it; null when the region is too small.</summary>
    internal static InstallerPlacement? PlaceInstaller(FreeRegion region, long diskSizeBytes, long installerBytes, long linuxBytes)
    {
        if (region.Size <= 0 || installerBytes <= 0 || linuxBytes <= 0 || diskSizeBytes <= 0)
            return null;
        var usableEnd = UsableEnd(region.Offset + region.Size, diskSizeBytes);
        var offset = usableEnd - RoundUpMiB(linuxBytes) - RoundUpMiB(installerBytes);
        return offset < RoundUpMiB(region.Offset) ? null : new(offset, usableEnd - offset - RoundUpMiB(installerBytes));
    }

    /// <summary>The place in unallocated space as it is: the gap behind Windows first, because an earlier attempt made it, then the largest region.</summary>
    internal static InstallerPlacement? PlaceInExistingSpace(IReadOnlyList<LayoutPartition> partitions, long diskSizeBytes,
        long installerBytes, long linuxBytes)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var regions = FreeRegions(partitions.Select(p => (p.Offset, p.Size)), diskSizeBytes);
        if (regions.Count == 0) return null;
        var windows = partitions.Where(p => p.IsWindows).ToArray();
        if (windows.Length == 1)
            foreach (var behind in regions.Where(region => region.Offset == windows[0].Offset + windows[0].Size))
                if (PlaceInstaller(behind, diskSizeBytes, installerBytes, linuxBytes) is { } placed)
                    return placed;
        return PlaceInstaller(regions.MaxBy(region => region.Size), diskSizeBytes, installerBytes, linuxBytes);
    }

    /// <summary>The plan for an installer that is bound to the space behind the installer partition; other unallocated regions do not matter to it.</summary>
    internal static BoundLinuxPlan PlanBoundLinuxRegion(int diskNumber, IReadOnlyList<LayoutPartition> partitions,
        long diskSizeBytes, long linuxBytes, long installerBytes, InstallerTargetRules rules)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(rules);
        if (diskSizeBytes <= 0 || linuxBytes <= 0 || installerBytes <= 0 || !rules.BoundToInstallerPartition ||
            rules.MinimumRegionBytes <= 0)
            throw new InvalidOperationException(
                $"The partition layout of disk {diskNumber} could not be evaluated. Windows has not been resized.");
        if (linuxBytes < rules.MinimumRegionBytes)
            throw new InstallationRefusedException(FormattableString.Invariant(
                $"Linux needs {rules.MinimumRegionBytes / (1024.0 * 1024 * 1024):N0} GiB or more. Windows has not been resized."));
        // Space an earlier attempt already took from Windows is used as it is; Windows is not shrunk for it again.
        if (PlaceInExistingSpace(partitions, diskSizeBytes, installerBytes, linuxBytes) is { } existing)
            return new(0, existing);
        var planned = checked(linuxBytes + installerBytes);
        var windows = partitions.Where(p => p.IsWindows).ToArray();
        if (windows.Length != 1)
            throw new InstallationRefusedException(FormattableString.Invariant(
                $"Disk {diskNumber} has no unallocated region of {planned / (1024.0 * 1024 * 1024):N1} GiB and no single Windows partition that can be shrunk to make one. Windows has not been resized."));
        // A gap that already lies behind Windows counts: Windows gives up only what is missing.
        var windowsEnd = windows[0].Offset + windows[0].Size;
        var gapEnd = windowsEnd + FreeBytesBehind(partitions, diskSizeBytes, windowsEnd);
        var target = UsableEnd(gapEnd, diskSizeBytes) - RoundUpMiB(linuxBytes) - RoundUpMiB(installerBytes);
        var shrink = windowsEnd - target;
        if (shrink <= 0 || target <= windows[0].Offset)
            throw new InstallationRefusedException(FormattableString.Invariant(
                $"Disk {diskNumber} has no unallocated region of {planned / (1024.0 * 1024 * 1024):N1} GiB and no single Windows partition that can be shrunk to make one. Windows has not been resized."));
        return new(shrink, new(target, RoundUpMiB(linuxBytes)));
    }

    /// <summary>The unallocated space that starts where a partition ends; zero when another partition follows at once.</summary>
    internal static long FreeBytesBehind(IReadOnlyList<LayoutPartition> partitions, long diskSizeBytes, long partitionEnd)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        return FreeRegions(partitions.Select(p => (p.Offset, p.Size)), diskSizeBytes)
            .Where(region => region.Offset == partitionEnd).Select(region => region.Size).SingleOrDefault();
    }

    /// <summary>Refuses when another disk offers the installer a region at least as large as the planned one.</summary>
    internal static void RequireNoRivalRegion(int diskNumber, long expectedLinuxBytes,
        IEnumerable<(int DiskNumber, FreeRegion Region)> otherDiskRegions)
    {
        ArgumentNullException.ThrowIfNull(otherDiskRegions);
        foreach (var (other, region) in otherDiskRegions)
            if (other != diskNumber && region.Size >= expectedLinuxBytes)
                throw new InstallationRefusedException(FormattableString.Invariant(
                    $"Disk {other} has an unallocated region of {region.Size / (1024.0 * 1024 * 1024):N1} GiB, and this installer takes the largest one on any disk. ") +
                    FormattableString.Invariant($"The region planned on disk {diskNumber} is {expectedLinuxBytes / (1024.0 * 1024 * 1024):N1} GiB. ") +
                    $"Windows has not been resized. Disconnect disk {other} or allocate that space, then start again.");
    }

    // Checks the live layout, then shrinks Windows only when the unallocated space is too small.
    internal void ReserveLinuxSpace(int diskNumber, long linuxSizeBytes, DirectInstallSizePlan sizing,
        InstallerBootSpec bootSpec, InstallerTargetRules targetRules,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct) =>
        _ = ReserveLinuxSpace(diskNumber, linuxSizeBytes, sizing, bootSpec, targetRules, null, prog, ct);

    /// <returns>True when Windows was shrunk.</returns>
    internal bool ReserveLinuxSpace(int diskNumber, long linuxSizeBytes, DirectInstallSizePlan sizing,
        InstallerBootSpec bootSpec, InstallerTargetRules targetRules, BoundLinuxPlan? bound,
        IProgress<DirectInstallProgress>? prog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sizing);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var totalShrink = sizing.TotalShrinkBytes;
        // A pre-created root partition takes the region; that installer is handed the partition.
        var plan = bound is not null || bootSpec.PreCreateRootPartition ? null
            : RequireSingleLinuxRegion(diskNumber, totalShrink, sizing.OemDrvBytes + sizing.IsoPartitionBytes,
                reusesInstallerPartition: false, [], targetRules);
        if (bound is not null)
        {
            // The bound plan found the place first and takes from Windows only what that place still lacks.
            if (!bound.ShrinksWindows)
            {
                _logger.LogInformation("Direct install: disk {Disk} already has unallocated space for {NeedGiB:N1} GiB - Windows is not shrunk",
                    diskNumber, totalShrink / (1024.0 * 1024 * 1024));
                Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Using existing free space; Windows is not resized…");
                return false;
            }
            _logger.LogInformation("Direct install: shrinking Windows on disk {Disk} by {MiB} MiB for {NeedGiB:N1} GiB of Linux and installer",
                diskNumber, bound.ShrinkBytes / MiB, totalShrink / (1024.0 * 1024 * 1024));
            _resizer.ShrinkAsync(diskNumber, bound.ShrinkBytes, new Progress<string>(msg =>
                Report(prog, DirectInstallPhase.ShrinkingPartition, message: msg)), ct).GetAwaiter().GetResult();
            return true;
        }
        var freeExtent = plan?.LargestFreeBytes ?? LargestFreeExtentBytes(diskNumber);
        if (freeExtent >= totalShrink)
        {
            _logger.LogInformation("Direct install: disk {Disk} already has {GiB:N1} GiB unallocated (need {NeedGiB:N1} GiB) - Windows is not shrunk",
                diskNumber, freeExtent / (1024.0 * 1024 * 1024), totalShrink / (1024.0 * 1024 * 1024));
            Report(prog, DirectInstallPhase.ShrinkingPartition, message: "Using existing free space; Windows is not resized…");
            return false;
        }
        _logger.LogInformation("Direct install: shrinking disk {Disk} by {GiB} GiB",
            diskNumber, linuxSizeBytes / MiB / 1024);
        var shrinkProg = new Progress<string>(msg =>
            Report(prog, DirectInstallPhase.ShrinkingPartition, message: msg));
        _resizer.ShrinkAsync(diskNumber, totalShrink, shrinkProg, ct).GetAwaiter().GetResult();
        return true;
    }

    // The partitions of one disk with their numbers; letters of installer partitions this run removes are left out.
    private (List<(uint Number, LayoutPartition Partition)> Partitions, long DiskSize) ReadDiskLayout(
        int diskNumber, IReadOnlyCollection<char> ignoredLetters)
    {
        try
        {
            var disk = _storage.ReadDisks().RowsOrThrow().Single(row =>
                Convert.ToInt32(row["Number"], CultureInfo.InvariantCulture) == diskNumber);
            var partitions = new List<(uint, LayoutPartition)>();
            foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
            {
                var letter = WmiValues.ToDriveLetter(p["DriveLetter"]);
                if (letter != '\0' && ignoredLetters.Contains(char.ToUpperInvariant(letter)))
                    continue;
                partitions.Add((p.Properties.TryGetValue("PartitionNumber", out var number) && number is not null
                        ? Convert.ToUInt32(number, CultureInfo.InvariantCulture) : 0U,
                    new(Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture),
                        Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture),
                        p["IsBoot"] is true && char.IsAsciiLetter(letter),
                        string.Equals(p["GptType"]?.ToString(), EspGptType, StringComparison.OrdinalIgnoreCase))));
            }
            return (partitions, Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException
                                   or InvalidCastException or InvalidOperationException or KeyNotFoundException)
        {
            // Not a refusal: this is also read after the disk was changed.
            throw new InvalidOperationException($"The partition layout of disk {diskNumber} could not be read.", ex);
        }
    }

    // Judged before the first write, for an installer bound to the space behind the installer partition.
    internal BoundLinuxPlan RequireBoundLinuxRegion(int diskNumber, long linuxBytes, long installerBytes,
        IReadOnlyCollection<char> staleSeedLetters, InstallerTargetRules rules)
    {
        List<LayoutPartition> layout;
        long diskSize;
        try
        {
            (var partitions, diskSize) = ReadDiskLayout(diskNumber, staleSeedLetters);
            layout = partitions.Select(p => p.Partition).ToList();
        }
        catch (InvalidOperationException ex)
        {
            throw new InstallationRefusedException(
                $"The partition layout of disk {diskNumber} could not be read. Windows has not been resized.", ex);
        }
        var plan = PlanBoundLinuxRegion(diskNumber, layout, diskSize, linuxBytes, installerBytes, rules);
        _logger.LogInformation(
            "Linux region: the installer partition goes at offset {Offset} on disk {Disk}, with {GiB:N1} GiB for Linux behind it (Windows gives up {ShrinkMiB} MiB)",
            plan.Placement.InstallerOffset, diskNumber, plan.Placement.LinuxBytes / (1024.0 * 1024 * 1024), plan.ShrinkBytes / MiB);
        return plan;
    }

    // Placed on the layout as it is after the shrink, not as it was planned.
    internal InstallerPlacement RequireInstallerPlacement(int diskNumber, long linuxBytes, long installerBytes)
    {
        var (partitions, diskSize) = ReadDiskLayout(diskNumber, []);
        return PlaceInExistingSpace(partitions.Select(p => p.Partition).ToList(), diskSize, installerBytes, linuxBytes)
            ?? throw new InvalidOperationException(
                $"The space for Linux and the installer on disk {diskNumber} is not where it was planned. Nothing was written to it; a new attempt uses the space that is there.");
    }

    /// <summary>The unallocated space directly behind a partition; null when that partition is not found once.</summary>
    internal long? FreeBytesBehindPartition(int diskNumber, uint partitionNumber)
    {
        var (partitions, diskSize) = ReadDiskLayout(diskNumber, []);
        var match = partitions.Where(p => p.Number == partitionNumber && partitionNumber != 0).Select(p => p.Partition).ToArray();
        return match.Length == 1
            ? FreeBytesBehind(partitions.Select(p => p.Partition).ToList(), diskSize, match[0].Offset + match[0].Size)
            : null;
    }

    internal enum BoundSeed { Reuse, Replace, Refuse }

    /// <summary>What to do with an installer partition of an earlier run, from the unallocated space directly behind it.</summary>
    internal static BoundSeed JudgeBoundSeed(long? freeBehindBytes, long linuxBytes, long minimumRegionBytes)
    {
        var asked = RoundUpMiB(linuxBytes);
        if (freeBehindBytes is { } free && free >= asked && free <= asked + LinuxExtentSlackBytes)
            return BoundSeed.Reuse;
        // Less than a Linux fits behind it: an earlier installation sits there and is removed first, in System Check.
        return (freeBehindBytes ?? 0) < minimumRegionBytes ? BoundSeed.Refuse : BoundSeed.Replace;
    }

    /// <summary>The bounds the installer is given when the space directly behind a partition is the Linux size asked; null when it is not.</summary>
    internal (long Minimum, long Maximum)? BoundExtentBehind(int diskNumber, uint partitionNumber, long linuxBytes)
    {
        var extent = FreeBytesBehindPartition(diskNumber, partitionNumber);
        var asked = RoundUpMiB(linuxBytes);
        _logger.LogInformation("Disk {Disk}: {GiB:N2} GiB is unallocated directly behind partition {Partition}; {AskedGiB:N2} GiB was asked for Linux",
            diskNumber, (extent ?? 0) / (1024.0 * 1024 * 1024), partitionNumber, asked / (1024.0 * 1024 * 1024));
        return JudgeBoundSeed(extent, linuxBytes, 0) == BoundSeed.Reuse
            ? (asked - 2 * MiB, asked + LinuxExtentSlackBytes) : null;
    }

    // Reads the layout and applies the rules; staleSeedLetters are volumes this run removes first.
    internal LinuxRegionPlan RequireSingleLinuxRegion(int diskNumber, long plannedBytes, long installerBytes,
        bool reusesInstallerPartition, IReadOnlyCollection<char> staleSeedLetters, InstallerTargetRules rules)
    {
        List<LayoutPartition> partitions = [];
        List<(int DiskNumber, FreeRegion Region)> elsewhere = [];
        long diskSize = 0;
        try
        {
            foreach (var disk in _storage.ReadDisks().RowsOrThrow())
            {
                var number = Convert.ToInt32(disk["Number"], CultureInfo.InvariantCulture);
                var size = Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture);
                if (number == diskNumber)
                {
                    diskSize = size;
                    foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
                    {
                        var letter = WmiValues.ToDriveLetter(p["DriveLetter"]);
                        if (letter != '\0' && staleSeedLetters.Contains(char.ToUpperInvariant(letter)))
                            continue;
                        partitions.Add(new(Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture),
                            Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture),
                            p["IsBoot"] is true && char.IsAsciiLetter(letter),
                            string.Equals(p["GptType"]?.ToString(), EspGptType, StringComparison.OrdinalIgnoreCase)));
                    }
                }
                else if (rules.ScansAllDisks && size > 0 && !(disk.Properties.TryGetValue("BusType", out var bus) &&
                    bus is not null && Convert.ToUInt32(bus, CultureInfo.InvariantCulture) == FileBackedVirtualBus))
                {
                    var layout = _storage.ReadPartitions(number).RowsOrThrow()
                        .Select(p => (Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture),
                            Convert.ToInt64(p["Size"], CultureInfo.InvariantCulture)));
                    elsewhere.AddRange(FreeRegions(layout, size).Select(region => (number, region)));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException
                                   or InvalidCastException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InstallationRefusedException(
                $"The partition layout of disk {diskNumber} could not be read. Windows has not been resized.", ex);
        }

        var plan = PlanLinuxRegion(diskNumber, partitions, diskSize, plannedBytes, installerBytes,
            reusesInstallerPartition, rules);
        RequireNoRivalRegion(diskNumber, plan.ExpectedLinuxBytes, elsewhere);
        _logger.LogInformation(
            "Linux region: disk {Disk} offers one unallocated region of {GiB:N1} GiB at offset {Offset} after the plan (Windows shrink: {Shrinks})",
            diskNumber, plan.Region.Size / (1024.0 * 1024 * 1024), plan.Region.Offset, plan.ShrinksWindows);
        return plan;
    }
}
