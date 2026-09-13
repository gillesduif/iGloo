using Igloo.Core.Models;

namespace Igloo.Core.Services;

/// <summary>An offered free extent, not an installation authorization.</summary>
public sealed record InstallationFreeExtent(long OffsetBytes, long LengthBytes);

/// <summary>Offers all suitable gaps in a reviewed snapshot; never chooses a target.</summary>
public static class InstallationTargetAllocation
{
    private const long MiB = 1024 * 1024;

    public static IReadOnlyList<InstallationFreeExtent> GetFreeExtents(GptDiskLayout layout, long minimumBytes)
    {
        InstallationTargetValidation.ValidateLayout(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytes);
        var extents = new List<InstallationFreeExtent>();
        var cursor = MiB;
        var limit = layout.DiskSizeBytes - MiB;
        foreach (var partition in layout.Partitions.OrderBy(p => p.OffsetBytes))
        {
            AddGap(Math.Min(partition.OffsetBytes, limit));
            cursor = Math.Max(cursor, partition.OffsetBytes + partition.LengthBytes);
        }
        AddGap(limit);
        return extents.AsReadOnly();

        void AddGap(long end)
        {
            // Round only the offered boundaries; no operation silently rounds an
            // already authorized target. Division avoids overflow near Int64.MaxValue.
            var start = cursor / MiB * MiB;
            if (start < cursor)
            {
                if (start > long.MaxValue - MiB)
                    return;
                start += MiB;
            }
            var alignedEnd = end / MiB * MiB;
            if (alignedEnd > start && alignedEnd - start >= minimumBytes)
                extents.Add(new InstallationFreeExtent(start, alignedEnd - start));
        }
    }

    public static InstallationTargetRequest Authorize(
        Guid installationId, GptDiskLayout reviewedLayout, InstallationFreeExtent selectedExtent,
        long rootLengthBytes, Guid espPartitionGuid)
    {
        ArgumentNullException.ThrowIfNull(selectedExtent);
        ArgumentNullException.ThrowIfNull(reviewedLayout);
        if (rootLengthBytes <= 0 || rootLengthBytes % MiB != 0)
            throw new ArgumentOutOfRangeException(nameof(rootLengthBytes));
        if (!GetFreeExtents(reviewedLayout, rootLengthBytes).Contains(selectedExtent))
            throw new InvalidDataException("The selected free extent is absent or cannot hold the requested root.");
        if (!reviewedLayout.Partitions.Any(p => p.PartitionGuid == espPartitionGuid
                && p.GptType == InstallationTargetValidation.EfiSystemType))
            throw new InvalidDataException("Select the exact existing EFI System Partition.");
        var request = new InstallationTargetRequest
        {
            InstallationId = installationId,
            ExpectedLayout = reviewedLayout with { Partitions = reviewedLayout.Partitions.ToArray() },
            RootOffsetBytes = selectedExtent.OffsetBytes,
            RootLengthBytes = rootLengthBytes,
            EspPartitionGuid = espPartitionGuid,
        };
        InstallationTargetValidation.ValidateRequest(request);
        return request;
    }
}
