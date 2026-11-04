using System.IO;
using System.Text.Json;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Preparation;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Community.App;

/// <summary>Obtains durable advisory IDs through the packaged CLI, then pins the private reviewed plan.</summary>
public interface ICommunityReviewedInstallationWorkflow
{
    Task<ReviewedInstallationPlanV1> ReviewAsync(string distroId, DiskInfo selectedDisk,
        long requestedLinuxBytes, IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default);

    Task<PreparationCheckpointReference> PrepareAsync(string distroId,
        ReviewedInstallationPlanV1 reviewed, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class CliReviewedInstallationWorkflow(
    CliCommandClient client, IReviewedInstallationPlanning planning,
    IReviewedInstallationSizing sizing,
    IReviewedInstallationPreparation preparation) : ICommunityReviewedInstallationWorkflow
{
    public async Task<ReviewedInstallationPlanV1> ReviewAsync(string distroId,
        DiskInfo selectedDisk, long requestedLinuxBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(selectedDisk);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (selectedDisk.GptDiskGuid is not { } diskGuid || diskGuid == Guid.Empty ||
            !string.Equals(selectedDisk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            selectedDisk.TotalBytes <= 0 || !selectedDisk.Partitions.Any(p => p.IsBoot) ||
            requestedLinuxBytes <= 0 || !verifiedIso.Sha256Verified ||
            verifiedIso.VerifiedSha256 is not { Length: 64 } digest ||
            !digest.All(Uri.IsHexDigit) || !Path.IsPathFullyQualified(verifiedIso.LocalPath) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new InvalidDataException("The selected disk, installer image or staged files lack a verified identity.");

        // The GUI slider describes Linux root bytes. The reviewed layout also
        // needs an ESP, payload, optional ISO partition and alignment room.
        // Ask Host for its pinned media/staging measurement before issuing any
        // advisory allocation ID, so the CLI allocates the complete layout.
        var measured = await sizing.MeasureAsync(distroId, requestedLinuxBytes,
            verifiedIso, stagingDirectory, cancellationToken).ConfigureAwait(false);
        if (measured.MinimumShrinkBytes <= requestedLinuxBytes ||
            measured.Sizing.TotalShrinkBytes <= requestedLinuxBytes ||
            measured.StagedInventorySha256 is not { Length: 64 } inventoryHash ||
            !inventoryHash.All(Uri.IsHexDigit) || measured.StagedBytes <= 0)
            throw new InvalidDataException("The measured installer layout has no complete shrink requirement.");

        var advisory = await ExecuteAsync<MigrationPlanResultV2>("migration.plan",
            new MigrationPlanInput(distroId, requestedLinuxBytes), 2,
            cancellationToken).ConfigureAwait(false);
        if (advisory.PlanId == Guid.Empty || advisory.DistroId != distroId ||
            advisory.RequiredBytes > requestedLinuxBytes || advisory.BlockerCodes is null ||
            advisory.BlockerCodes.Count != 0 ||
            advisory.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            advisory.Executable || advisory.Disposition != "AdvisoryOnly")
            throw new InvalidOperationException("The current system check cannot support a reviewed installer layout.");

        var target = await ExecuteAsync<MigrationTargetBinding>("migration.target.bind",
            new PlanSelection(advisory.PlanId), 1, cancellationToken).ConfigureAwait(false);
        if (target.PlanId != advisory.PlanId || target.TargetId == Guid.Empty ||
            target.BoundStateFingerprint != advisory.ObservedStateFingerprint ||
            target.Executable)
            throw new InvalidDataException("The advisory target does not match the current plan.");

        var allocation = await ExecuteAsync<MigrationAllocationResult>("migration.target.allocate",
            AllocationInput(advisory.PlanId, target.TargetId, requestedLinuxBytes,
                measured), 1, cancellationToken).ConfigureAwait(false);
        if (allocation.PlanId != advisory.PlanId || allocation.TargetId != target.TargetId ||
            allocation.AllocationId == Guid.Empty ||
            allocation.Scope != "WindowsBootNtfsShrinkIntentV1" ||
            allocation.RequestedShrinkBytes != measured.MinimumShrinkBytes ||
            allocation.PlannedShrinkBytes < measured.MinimumShrinkBytes ||
            allocation.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            allocation.Executable)
            throw new InvalidDataException("The exact advisory allocation does not match the selected size.");

        var reviewed = await planning.PlanAsync(distroId, advisory.PlanId,
            target.TargetId, allocation.AllocationId, requestedLinuxBytes,
            verifiedIso, stagingDirectory, cancellationToken).ConfigureAwait(false);
        var intent = reviewed.Plan.ReviewedIntent;
        if (PreparedLayoutRules.VerifyReviewedIntent(reviewed.Plan).Availability !=
                ObservationAvailability.Available || intent is null ||
            reviewed.Checkpoint.GenerationId != reviewed.Plan.GenerationId ||
            intent.PlanId != advisory.PlanId || intent.TargetId != target.TargetId ||
            intent.AllocationId != allocation.AllocationId ||
            intent.ShrinkBytes != (ulong)allocation.PlannedShrinkBytes ||
            reviewed.Sizing != measured.Sizing ||
            reviewed.StagedBytes != measured.StagedBytes ||
            !string.Equals(reviewed.StagedInventorySha256,
                measured.StagedInventorySha256, StringComparison.Ordinal) ||
            reviewed.Plan.TargetDisk.GptDiskGuid != diskGuid ||
            reviewed.Plan.TargetDisk.SizeBytes != (ulong)selectedDisk.TotalBytes)
            throw new InvalidDataException("The reviewed layout does not match the selected disk and allocation.");
        return reviewed;
    }

    internal static MigrationAllocationInput AllocationInput(Guid planId, Guid targetId,
        long requestedLinuxBytes, ReviewedInstallationShrinkV1 measured)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (planId == Guid.Empty || targetId == Guid.Empty || requestedLinuxBytes <= 0 ||
            measured.MinimumShrinkBytes <= requestedLinuxBytes)
            throw new InvalidDataException("The reviewed allocation does not cover the complete installer layout.");
        return new(planId, targetId, measured.MinimumShrinkBytes);
    }

    public Task<PreparationCheckpointReference> PrepareAsync(string distroId,
        ReviewedInstallationPlanV1 reviewed, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        preparation.PrepareAsync(distroId, reviewed, requestedLinuxBytes, verifiedIso,
            stagingDirectory, progress, cancellationToken);

    private async Task<T> ExecuteAsync<T>(string capabilityId, object input,
        int version, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync(CliCommandClient.Request(capabilityId,
            input, capabilityVersion: version), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException($"The local {capabilityId} review stopped: " +
                (result.Error?.Code.ToString() ?? "UnknownOutcome"));
        if (result.Data is null)
            throw new InvalidDataException($"The local {capabilityId} review returned no result.");
        try
        {
            return result.Data.Value.Deserialize<T>(CommandProtocol.JsonOptions) ??
                throw new InvalidDataException($"The local {capabilityId} review returned no result.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The local {capabilityId} review returned an invalid result.", error);
        }
    }
}
