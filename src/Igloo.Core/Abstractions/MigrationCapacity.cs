namespace Igloo.Core.Abstractions;

/// <summary>Advisory capacity observed on the single Windows boot disk. This is not a target binding or permission to shrink.</summary>
public enum MigrationCapacityState
{
    KnownAvailable,
    Insufficient,
    PhysicalDiskTooSmall,
    Unknown,
    AmbiguousTarget,
}

public sealed record MigrationCapacity(MigrationCapacityState State, long? ObservedAvailableBytes,
    int WindowsBootDiskCount);

public static class MigrationCapacityEvaluator
{
    public static MigrationCapacity Evaluate(IReadOnlyList<DiskInfo> disks, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        var candidates = disks.Where(d => d.Partitions.Any(p => p.IsBoot)).ToArray();
        if (candidates.Length != 1)
            return new(MigrationCapacityState.AmbiguousTarget, null, candidates.Length);

        var disk = candidates[0];
        if (disk.TotalBytes <= 0)
            return new(MigrationCapacityState.Unknown, null, 1);
        if (disk.TotalBytes < requiredBytes)
            return new(MigrationCapacityState.PhysicalDiskTooSmall, disk.TotalBytes, 1);

        var shrinkable = disk.Partitions.Where(p => p.IsBoot)
            .Select(p => p.ShrinkableBytes).DefaultIfEmpty(0).Max();
        // Zero shrinkability may mean the storage query failed. Do not call that insufficient.
        if (disk.FreeBytes < requiredBytes && shrinkable <= 0)
            return new(MigrationCapacityState.Unknown, null, 1);
        var available = Math.Max(disk.FreeBytes, shrinkable);
        return new(available < requiredBytes ? MigrationCapacityState.Insufficient
            : MigrationCapacityState.KnownAvailable, available, 1);
    }
}
