namespace Igloo.Core.Abstractions;

/// <summary>
/// Queries how much a Windows NTFS partition can be shrunk and performs the resize.
/// Required for the <see cref="DiskInstallMode.DualBoot"/> path - Linux needs
/// unpartitioned free space that Anaconda can claim.
/// </summary>
public interface IPartitionResizeService
{
    /// <summary>
    /// Returns the number of bytes by which the largest NTFS partition on
    /// <paramref name="diskNumber"/> can be shrunk (i.e. how much space is available
    /// for a Linux partition without data loss).  Returns 0 if no shrinkable partition
    /// is found or the query fails.
    /// </summary>
    Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default);

    /// <summary>
    /// Shrinks the main NTFS partition on <paramref name="diskNumber"/> so that
    /// <paramref name="linuxSizeBytes"/> of unallocated space is freed for Linux.
    /// Requires the process to be running with administrator privileges.
    /// </summary>
    Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
