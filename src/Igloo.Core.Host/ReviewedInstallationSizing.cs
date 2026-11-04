using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Preflight;

namespace Igloo.Core.Host;

/// <summary>Read-only shrink sizing for a reviewed provider root layout.</summary>
public sealed record ReviewedInstallationShrinkV1(long MinimumShrinkBytes,
    ReviewedInstallerSizingV1 Sizing, long StagedBytes, string StagedInventorySha256);

public interface IReviewedInstallationSizing
{
    Task<ReviewedInstallationShrinkV1> MeasureAsync(string distroId,
        long requestedLinuxBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, CancellationToken cancellationToken = default);
}

internal sealed class ReviewedInstallationSizing(IReviewedInstallationMedia media,
    DistroRegistry registry) : IReviewedInstallationSizing
{
    private const long MinimumRootBytes = 20L * 1024 * 1024 * 1024;
    private const long GiB = 1024L * 1024 * 1024;

    public async Task<ReviewedInstallationShrinkV1> MeasureAsync(string distroId,
        long requestedLinuxBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (requestedLinuxBytes <= 0 || requestedLinuxBytes % GiB != 0 ||
            !registry.TryGet(distroId, out var provider) ||
            requestedLinuxBytes < Math.Max(MinimumRootBytes,
                provider.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected provider or Linux root size is unavailable.");
        ReviewedInstallationProviderSupport.RequirePlanning(provider);
        var pinned = await media.AcquireAndInspectAsync(distroId, verifiedIso,
            cancellationToken).ConfigureAwait(false);
        var bootSpec = ReviewedInstallationProviderSupport.ReviewedBootSpec(provider) with
        { IsoSha256 = pinned.IsoSha256 };
        var staged = ReviewedMediaInspector.InspectStagedContent(stagingDirectory,
            cancellationToken);
        var sizing = ReviewedMediaInspector.PlanStorageSizes(bootSpec,
            requestedLinuxBytes, pinned.Measurements, staged.TotalBytes);
        if (sizing.TotalShrinkBytes <= 0 || sizing.PayloadMinimumBytes <= 0 ||
            sizing.IsoMinimumBytes < 0)
            throw new InvalidDataException("The reviewed installer media sizing is invalid.");

        // SpacePlanning adds a dedicated 1 GiB Linux ESP before the installer
        // payload and may align the free-extent start by less than one MiB.
        var minimumShrinkBytes = checked(sizing.TotalShrinkBytes +
            (long)PreparationSpacePlanning.LinuxEspBytes +
            (long)PreparationSpacePlanning.Alignment);
        return new(minimumShrinkBytes, sizing, staged.TotalBytes,
            staged.InventorySha256);
    }
}
