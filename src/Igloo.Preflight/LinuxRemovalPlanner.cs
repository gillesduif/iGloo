using System.Globalization;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

/// <summary>What one removal will touch, decided from a layout read before the first delete.</summary>
/// <param name="Partitions">Rows to delete, each found at the offset and size System Check reported.</param>
/// <param name="UnmatchedPartitions">Reported partitions that are no longer where they were; none of them is deleted.</param>
/// <param name="BootEntries">Firmware entries to delete, chosen by the partition their device path names.</param>
/// <param name="BootCleanupDisks">Disks whose EFI system partitions lose their Linux loader folders; empty when skipped.</param>
/// <param name="SkippedBootCleanup">Why the boot cleanup is skipped although every detected Linux goes; null otherwise.</param>
internal sealed record LinuxRemovalPlan(
    IReadOnlyList<(uint Disk, WindowsStorageRow Row, string Description)> Partitions,
    int UnmatchedPartitions,
    IReadOnlyList<ushort> BootEntries,
    IReadOnlyList<uint> BootCleanupDisks,
    string? SkippedBootCleanup);

// Decides a removal without touching the disk: nothing outside the selected disks, nothing iGloo cannot place.
internal static class LinuxRemovalPlanner
{
    // Partition types Windows itself creates. Any other type that stays may belong to a system iGloo cannot see.
    private static readonly HashSet<string> WindowsGptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", // EFI system
        "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", // Microsoft reserved
        "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", // basic data
        "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}", // Windows recovery
        "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}", // LDM metadata
        "{af9b60a0-1431-4f62-bc68-3311714a69ad}", // LDM data
        "{e75caf8f-f680-4cee-afa3-b001e56efc2d}", // Storage Spaces
    };

    private static readonly HashSet<int> WindowsMbrTypes = [0x01, 0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0E, 0x0F, 0x27, 0x42, 0xEE];

    internal static LinuxRemovalPlan Plan(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<(uint Disk, WindowsStorageRow Row)> ownedSeeds, bool removingAllLinux,
        IReadOnlyList<WindowsStorageRow> layout, IReadOnlyList<EfiBootEntries.BootEntryTarget> entries)
    {
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(ownedSeeds);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(entries);

        var partitions = new List<(uint Disk, WindowsStorageRow Row, string Description)>();
        var unmatched = 0;
        foreach (var install in installations)
        {
            // Back to front, as before: a provider that renumbers after a delete keeps the rest valid.
            foreach (var reported in install.Partitions.OrderByDescending(p => p.OffsetBytes))
            {
                var found = layout.Where(row => Number(row, "DiskNumber") == install.DiskNumber &&
                    Number(row, "Offset") == reported.OffsetBytes && Number(row, "Size") == reported.SizeBytes &&
                    string.Equals(Text(row, "GptType"), reported.GptType, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (reported.OffsetBytes >= 0 && found.Length == 1)
                    partitions.Add((install.DiskNumber, found[0], $"{install.DisplayName} from {install.DiskModel}"));
                else
                    unmatched++;
            }
        }
        partitions.AddRange(ownedSeeds.Select(seed => (seed.Disk, seed.Row, "the iGloo installer partition")));

        var linuxDisks = installations.Select(install => install.DiskNumber).ToHashSet();
        var touchedDisks = linuxDisks.Concat(ownedSeeds.Select(seed => seed.Disk)).ToHashSet();
        var staying = layout.Where(row => !partitions.Any(deleted => ReferenceEquals(deleted.Row, row))).ToArray();
        var unknown = staying.FirstOrDefault(row => !IsWindowsPartition(row) && !IsLinuxPartition(row));
        var linuxLeft = staying.FirstOrDefault(IsLinuxPartition);
        string? skipped = null;
        if (removingAllLinux && unmatched > 0)
            skipped = "a reported Linux partition is no longer where System Check found it";
        else if (removingAllLinux && (unknown ?? linuxLeft) is { } stranger)
            skipped = FormattableString.Invariant(
                $"partition {Number(stranger, "PartitionNumber")} on disk {Number(stranger, "DiskNumber")} ") +
                (unknown is null ? "is a Linux partition outside this removal" : "is of a type iGloo does not recognise");
        var cleansBoot = removingAllLinux && skipped is null;

        var partitionDisks = new Dictionary<Guid, uint>();
        foreach (var row in layout)
            if (Guid.TryParse(Text(row, "Guid"), out var id) && id != Guid.Empty && Number(row, "DiskNumber") is >= 0 and var disk)
                partitionDisks[id] = (uint)disk;

        var bootEntries = new List<ushort>();
        foreach (var entry in entries)
        {
            uint? disk = entry.PartitionGuid is { } id && partitionDisks.TryGetValue(id, out var found) ? found : null;
            if (EfiBootEntries.IsIglooDescription(entry.Description))
            {
                // iGloo's own one-shot entry: on a disk this removal touches, or pointing at a partition that is gone.
                if (entry.PartitionGuid is not null && (disk is null || touchedDisks.Contains(disk.Value)))
                    bootEntries.Add(entry.Index);
            }
            else if (EfiBootEntries.IsLinuxDescription(entry.Description) && disk is { } linuxDisk && linuxDisks.Contains(linuxDisk))
            {
                // With other Linux staying, only the entry System Check paired with a removed installation goes.
                if (cleansBoot || (unknown is null && unmatched == 0 && installations.Any(install =>
                        install.FirmwareEntryIndex == entry.Index && install.DiskNumber == linuxDisk)))
                    bootEntries.Add(entry.Index);
            }
        }

        return new(partitions, unmatched, bootEntries, cleansBoot ? [.. linuxDisks.Order()] : [], skipped);
    }

    internal static bool IsWindowsPartition(WindowsStorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var gptType = Text(row, "GptType");
        if (!string.IsNullOrEmpty(gptType))
            return WindowsGptTypes.Contains(gptType);
        return Number(row, "MbrType") is >= 0 and var mbrType && WindowsMbrTypes.Contains((int)mbrType);
    }

    internal static bool IsLinuxPartition(WindowsStorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Text(row, "GptType") is { Length: > 0 } gptType && WindowsPreflightChecker.LinuxGptTypes.Contains(gptType);
    }

    private static string? Text(WindowsStorageRow row, string name) =>
        row.Properties.TryGetValue(name, out var value) ? (value as string)?.Trim() : null;

    private static long Number(WindowsStorageRow row, string name) =>
        row.Properties.TryGetValue(name, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : -1;
}
