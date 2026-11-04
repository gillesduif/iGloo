using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime.Execution;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Core.Host;

/// <summary>Private first-party preparation boundary for a pinned reviewed plan.</summary>
public interface IReviewedInstallationPreparation
{
    Task<PreparationCheckpointReference> PrepareAsync(string distroId,
        ReviewedInstallationPlanV1 planned, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso,
        string stagingDirectory, IProgress<DirectInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ReviewedInstallationPreparation(
    IReviewedInstallationPlanning planning, IReviewedInstallationMedia media,
    IReviewedDirectInstallService service, DistroRegistry registry,
    DistroLoader loader) : IReviewedInstallationPreparation
{
    public async Task<PreparationCheckpointReference> PrepareAsync(string distroId,
        ReviewedInstallationPlanV1 planned, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso,
        string stagingDirectory, IProgress<DirectInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        var intent = planned.Plan.ReviewedIntent;
        if (PreparedLayoutRules.VerifyReviewedIntent(planned.Plan).Availability !=
                ObservationAvailability.Available || intent is null ||
            planned.Checkpoint.GenerationId != planned.Plan.GenerationId ||
            planned.StagedBytes < 0 || requestedLinuxBytes <= 0 ||
            planned.StagedInventorySha256 is not { Length: 64 } ||
            !planned.StagedInventorySha256.All(Uri.IsHexDigit) ||
            !string.Equals(planned.StagedInventorySha256,
                intent.StagedInventorySha256, StringComparison.Ordinal))
            throw new InvalidDataException("The selected reviewed installer checkpoint is invalid.");
        if (!registry.TryGet(distroId, out var provider) ||
            !loader.LoadedDistros.Any(d => d.IsAvailable && d.Id == distroId &&
                d.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal)))
            throw new NotSupportedException("The selected installer provider is unavailable.");
        ReviewedInstallationProviderSupport.RequirePlanning(provider);

        var pinned = await media.AcquireAndInspectAsync(distroId, verifiedIso,
            cancellationToken).ConfigureAwait(false);
        if (pinned.DistroId != distroId ||
            !string.Equals(pinned.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pinned.ProviderRevisionSha256,
                intent.ProviderRevisionSha256, StringComparison.Ordinal) ||
            pinned.Measurements != intent.Media ||
            !string.Equals(Path.GetFullPath(pinned.IsoPath),
                Path.GetFullPath(verifiedIso.LocalPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The current installer media or provider differs from the reviewed plan.");
        var footprint = PreparedLayoutRecoveryFootprint.Derive(planned.Plan);
        if (footprint.Availability != ObservationAvailability.Available ||
            footprint.Value is null)
            throw new InvalidDataException("The reviewed installer recovery footprint is unavailable.");
        var request = new ReviewedDirectInstallRequestV1(distroId, planned.Checkpoint,
            planned.Plan, planned.Plan.TargetDisk,
            ReviewedInstallationProviderSupport.ReviewedBootSpec(provider) with
                { IsoSha256 = pinned.IsoSha256 },
            requestedLinuxBytes,
            pinned.IsoPath, pinned.KernelPath, pinned.InitrdPath,
            stagingDirectory, planned.StagedBytes,
            planned.StagedInventorySha256, footprint.Value);
        return await service.PrepareReviewedAsync(request, provider,
            ct => planning.ResolveCurrentPlanAsync(planned.Plan, distroId,
                requestedLinuxBytes, verifiedIso, stagingDirectory,
                planned.StagedInventorySha256, ct), progress,
            cancellationToken).ConfigureAwait(false);
    }
}
