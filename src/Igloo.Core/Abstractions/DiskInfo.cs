namespace Igloo.Core.Abstractions;

/// <summary>A physical disk and its partitions, as reported by Windows.</summary>
public sealed record DiskInfo(string DeviceId, string Model, long TotalBytes, long FreeBytes,
    string PartitionStyle, IReadOnlyList<PartitionInfo> Partitions);

/// <summary>One partition on a disk.</summary>
/// <param name="ShrinkableBytes">How far the partition can shrink without data loss; 0 when unknown.</param>
/// <param name="OffsetBytes">
/// Byte position of the partition on the disk, -1 when the provider could not supply it.
/// Lets the UI render partitions (and the gaps between them) at their true positions,
/// Disk Management-style.
/// </param>
/// <param name="GptType">
/// The GPT partition-type GUID (braced string), null on MBR disks. Identifies
/// label-less service partitions (EFI, MSR, recovery, Linux).
/// </param>
public sealed record PartitionInfo(int Index, string FileSystem, long SizeBytes, string? Label,
    bool IsSystem, bool IsBoot, long ShrinkableBytes = 0, long OffsetBytes = -1, string? GptType = null);
