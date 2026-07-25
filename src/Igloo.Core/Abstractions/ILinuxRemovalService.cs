namespace Igloo.Core.Abstractions;


public interface ILinuxRemovalService
{
    Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
