using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    public Task<PreparationCheckpointReference> PrepareReviewedAsync(
        ReviewedDirectInstallRequestV1 request, IDistroPlugin provider,
        Func<CancellationToken, Task<PreparationPlanV1>> resolveCurrentPlan,
        IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default) =>
        ReviewNativeEntryAsync(request, provider, resolveCurrentPlan,
            new PreparedLayoutStore(CommunityRecoveryArtifactStore.ForCurrentUser()), ct);

    // The same boundary can be exercised with a disposable checkpoint store.
    // It performs no native mutation until exact restoration is implemented.
    internal static async Task<PreparationCheckpointReference> ReviewNativeEntryAsync(
        ReviewedDirectInstallRequestV1 request, IDistroPlugin provider,
        Func<CancellationToken, Task<PreparationPlanV1>> resolveCurrentPlan,
        PreparedLayoutStore checkpoints, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(resolveCurrentPlan);
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (string.IsNullOrWhiteSpace(request.DistroId) ||
            request.PlannedCheckpoint is null || request.ExpectedPlan is null ||
            request.TargetDisk is null || request.BootSpec is null ||
            request.ExpectedRecoveryFootprint is null ||
            request.RequestedLinuxBytes <= 0 || request.ExpectedStagedBytes <= 0 ||
            request.ExpectedStagedInventorySha256 is not { Length: 64 } ||
            !request.ExpectedStagedInventorySha256.All(Uri.IsHexDigit) ||
            !Path.IsPathFullyQualified(request.PinnedIsoPath) ||
            !Path.IsPathFullyQualified(request.StagingDirectory))
            throw new InvalidDataException("The reviewed installer request has incomplete pinned inputs.");
        var externalMedia = request.BootSpec.KernelUrl is not null &&
            request.BootSpec.InitrdUrl is not null;
        if ((request.BootSpec.KernelUrl is null) !=
            (request.BootSpec.InitrdUrl is null) ||
            (externalMedia && (string.IsNullOrWhiteSpace(request.PinnedKernelPath) ||
                string.IsNullOrWhiteSpace(request.PinnedRawInitrdPath) ||
                !Path.IsPathFullyQualified(request.PinnedKernelPath) ||
                !Path.IsPathFullyQualified(request.PinnedRawInitrdPath))) ||
            (!externalMedia && (request.PinnedKernelPath is not null ||
                request.PinnedRawInitrdPath is not null)))
            throw new InvalidDataException("The reviewed kernel and initrd source paths differ from the selected provider recipe.");

        var checkpoint = checkpoints.Reopen(request.PlannedCheckpoint);
        var plan = checkpoint.Plan;
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(request.ExpectedPlan);
        if (checkpoint.State != PreparationState.Planned ||
            checkpoint.ShrinkReceipt is not null || checkpoint.StorageOwnership is not null ||
            !checkpoint.CreationReceipts.IsEmpty ||
            !JsonSerializer.SerializeToUtf8Bytes(plan).AsSpan().SequenceEqual(expectedBytes) ||
            request.TargetDisk != plan.TargetDisk ||
            !string.Equals(provider.Id, request.DistroId, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed installer checkpoint, provider or exact disk changed.");
        var ownedRoot = plan.Space.Allocations.Single(a =>
            a.Role == PreparationRole.LinuxRoot).CreateInWindows;
        if (ownedRoot
                ? provider is not IReviewedInstallerStorageProvider or IReviewedInstallerConfigProvider
                : provider is not IReviewedInstallerConfigProvider or IReviewedInstallerStorageProvider)
            throw new NotSupportedException("The selected provider has no matching reviewed installer root contract.");
        var reviewed = ReviewPreparedIntent(plan, request.BootSpec,
            request.RequestedLinuxBytes);
        if (reviewed.Availability != ObservationAvailability.Available)
            throw new InvalidDataException($"The reviewed installer intent changed: {reviewed.Code}.");
        if (!string.Equals(plan.ReviewedIntent!.StagedInventorySha256,
                request.ExpectedStagedInventorySha256, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed staging inventory is not pinned in the checkpoint.");
        var declaredBoot = provider.GetInstallerBootSpec();
        if (ownedRoot)
            declaredBoot = declaredBoot with { PreCreateRootPartition = true };
        if (!JsonSerializer.SerializeToUtf8Bytes(request.BootSpec with
                { IsoSha256 = declaredBoot.IsoSha256 }).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(declaredBoot)))
            throw new InvalidDataException("The selected provider boot recipe changed.");

        var derived = PreparedLayoutRecoveryFootprint.Derive(plan);
        if (derived.Availability != ObservationAvailability.Available ||
            request.ExpectedRecoveryFootprint.GenerationId != plan.GenerationId ||
            request.ExpectedRecoveryFootprint.Mutations.IsDefault ||
            !request.ExpectedRecoveryFootprint.Mutations.SequenceEqual(
                derived.Value.Mutations) ||
            request.ExpectedRecoveryFootprint.MissingProofCodes.IsDefault ||
            !request.ExpectedRecoveryFootprint.MissingProofCodes.SequenceEqual(
                derived.Value.MissingProofCodes))
            throw new InvalidDataException("The reviewed storage recovery footprint changed.");

        async Task RequireCurrentPlanAsync()
        {
            ct.ThrowIfCancellationRequested();
            var current = await resolveCurrentPlan(ct).ConfigureAwait(false);
            if (current is null ||
                !JsonSerializer.SerializeToUtf8Bytes(current).AsSpan().SequenceEqual(
                    expectedBytes))
                throw new InvalidOperationException("The current installer target, allocation, artifact or provider changed.");
        }

        await RequireCurrentPlanAsync().ConfigureAwait(false);
        var measured = MeasureReviewedMedia(request.PinnedIsoPath, request.BootSpec,
            request.PinnedKernelPath, request.PinnedRawInitrdPath, ct);
        if (measured != plan.ReviewedIntent!.Media)
            throw new InvalidDataException("The pinned installer media differ from the reviewed plan.");
        await RequireCurrentPlanAsync().ConfigureAwait(false);

        var stage = Path.GetFullPath(request.StagingDirectory);
        if (!string.Equals(stage, request.StagingDirectory,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed installer staging path changed.");
        RequirePlainDirectoryTree(stage);
        var manifestPath = Path.Join(stage, "migration-manifest.json");
        RequirePlainFile(manifestPath);
        var manifestFile = new FileInfo(manifestPath);
        if (manifestFile.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("The reviewed migration manifest is oversized.");
        MigrationManifest manifest;
        try
        {
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct)
                .ConfigureAwait(false);
            if (manifestBytes.Length is <= 0 or > 8 * 1024 * 1024)
                throw new JsonException("The reviewed migration manifest has an invalid size.");
            manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes) ??
                throw new JsonException("The reviewed migration manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The reviewed migration manifest is invalid.", error);
        }
        const long GiB = 1024L * 1024 * 1024;
        if (manifest.SchemaVersion != 1 || manifest.Hardware is null ||
            manifest.User is null || manifest.Files is null ||
            !string.Equals(manifest.DistroId, request.DistroId,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.Hardware.InstallMode, "dual-boot",
                StringComparison.Ordinal) ||
            manifest.Hardware.TargetDiskGuid != plan.TargetDisk.GptDiskGuid ||
            plan.TargetDisk.SizeBytes > long.MaxValue ||
            manifest.Hardware.TargetDiskBytes != (long)plan.TargetDisk.SizeBytes ||
            request.RequestedLinuxBytes % GiB != 0 ||
            manifest.Hardware.LinuxPartitionSizeGb != request.RequestedLinuxBytes / GiB)
            throw new InvalidDataException("The reviewed migration manifest differs from the selected target.");
        var staged = ReviewedMediaInspector.InspectStagedContent(stage, ct);
        if (staged.TotalBytes != request.ExpectedStagedBytes ||
            !string.Equals(staged.InventorySha256,
                request.ExpectedStagedInventorySha256, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed staging content changed.");
        _ = ReviewedPartitionCreationService.RequireMeasuredSizing(plan,
            request.BootSpec, request.RequestedLinuxBytes, staged.TotalBytes);
        await RequireCurrentPlanAsync().ConfigureAwait(false);
        if (checkpoints.Reopen(request.PlannedCheckpoint).State != PreparationState.Planned)
            throw new InvalidOperationException("The reviewed installer checkpoint changed before preparation.");

        // The footprint is an inventory of proposed changes, never proof that
        // those changes can be restored. Keep the native writer closed until
        // every mutation surface has independently verified recovery.
        throw new NotSupportedException("Reviewed Windows preparation is blocked until the exact " +
            "storage and boot restoration proofs are available. No preparation changes were started. " +
            string.Join(", ", derived.Value.MissingProofCodes));
    }
}
