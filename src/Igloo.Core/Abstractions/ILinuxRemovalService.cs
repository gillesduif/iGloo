namespace Igloo.Core.Abstractions;


public interface ILinuxRemovalService
{
    Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Grows the disk's main Windows volume into free space that directly follows it.
    /// </summary>
    /// <remarks>
    /// Closes a loop that removal alone cannot. The first-boot agent deletes Igloo's
    /// staging partition from LINUX, where the NTFS volume in front of it cannot be
    /// resized - so the space is correctly left unallocated there, and until now nothing
    /// on the Windows side ever claimed it back. The user was left to finish the job in
    /// Disk Management, which is exactly the manual disk work Igloo exists to remove.
    ///
    /// Safe by construction: a volume can only be extended into ADJACENT trailing free
    /// space, so unallocated regions belonging to anything else are unreachable and
    /// nothing is moved or deleted.
    /// </remarks>
    /// <returns>Bytes reclaimed; zero when there was nothing adjacent to absorb.</returns>
    Task<long> ReclaimFreeSpaceAsync(uint diskNumber,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
