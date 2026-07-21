namespace Igloo.Core.Abstractions;

/// <summary>Removes existing Linux installations so the machine returns to a Windows-only state.</summary>
public interface ILinuxRemovalService
{
    /// <summary>
    /// Deletes the given installations' partitions, iGloo's stale boot entries and
    /// (optionally) leftover seed partitions. <paramref name="removingAllLinux"/>
    /// additionally clears every Linux-classified UEFI boot entry; only safe when
    /// no Linux partitions remain anywhere afterwards.
    /// </summary>
    Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
