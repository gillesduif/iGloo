using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Core.Host;

/// <summary>A persisted, read-only reviewed layout. This is not storage or boot authorization.</summary>
public sealed record ReviewedInstallationPlanV1(PreparationCheckpointReference Checkpoint,
    PreparationPlanV1 Plan, ReviewedInstallerSizingV1 Sizing, long StagedBytes,
    string StagedInventorySha256);

public interface IReviewedInstallationPlanning
{
    Task<ReviewedInstallationPlanV1> PlanAsync(string distroId, Guid planId,
        Guid targetId, Guid allocationId, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default);

    Task<PreparationPlanV1> ResolveCurrentPlanAsync(PreparationPlanV1 expectedPlan,
        string distroId, long requestedLinuxBytes, IsoAcquisitionResult verifiedIso,
        string stagingDirectory, string expectedStagedInventorySha256,
        CancellationToken cancellationToken = default);
}

internal static class ReviewedInstallationProviderSupport
{
    // The existing provider boot spec also serves USB and legacy preparation.
    // Fedora's reviewed storage contract requires a Windows-owned root, while
    // its existing USB recipe does not. Scope that root mode to reviewed
    // planning so the working USB recipe retains its declared behavior.
    internal static bool SupportsPlanning(IDistroPlugin provider) =>
        provider switch
        {
            IReviewedInstallerStorageProvider and IReviewedInstallerConfigProvider => false,
            IReviewedInstallerStorageProvider => true,
            IReviewedInstallerConfigProvider =>
                !provider.GetInstallerBootSpec().PreCreateRootPartition,
            _ => false,
        };

    internal static InstallerBootSpec ReviewedBootSpec(IDistroPlugin provider)
    {
        RequirePlanning(provider);
        var declared = provider.GetInstallerBootSpec();
        return provider is IReviewedInstallerStorageProvider
            ? declared with { PreCreateRootPartition = true }
            : declared;
    }

    internal static void RequirePlanning(IDistroPlugin provider)
    {
        if (SupportsPlanning(provider)) return;
        throw new NotSupportedException(
            "The selected provider has no reviewed installer root planning contract.");
    }

}

internal sealed class ReviewedInstallationPlanning(IAdvisoryPlanJournal journal,
    FileMigrationTargetRegistry targets, FileMigrationAllocationRegistry allocations,
    IAllocationObservationSource allocationSource, IWindowsStorageReader storage,
    IPreflightChecker preflight, DistroRegistry registry, DistroLoader loader,
    IReviewedInstallationMedia media, PreparedLayoutStore checkpoints)
    : IReviewedInstallationPlanning
{
    private const long GiB = 1024L * 1024 * 1024;

    public async Task<ReviewedInstallationPlanV1> PlanAsync(string distroId, Guid planId,
        Guid targetId, Guid allocationId, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        // Keep the final readback and checkpoint persistence under one local
        // recovery-state lease. This remains an evidence-only plan.
        using var lease = await ResourceLock.AcquireManyAsync(["recovery-state"],
            cancellationToken).ConfigureAwait(false);
        var (plan, sizing, stagedBytes, stagedInventorySha256) =
            await BuildCurrentPlanAsync(distroId,
            planId, targetId, allocationId, requestedLinuxBytes, verifiedIso,
            stagingDirectory, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        var reference = checkpoints.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, ImmutableArray<PreparedPartitionV1>.Empty, null));
        return new(reference, plan, sizing, stagedBytes, stagedInventorySha256);
    }

    public async Task<PreparationPlanV1> ResolveCurrentPlanAsync(
        PreparationPlanV1 expectedPlan, string distroId, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory,
        string expectedStagedInventorySha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPlan);
        var intent = expectedPlan.ReviewedIntent;
        if (PreparedLayoutRules.VerifyReviewedIntent(expectedPlan).Availability !=
                ObservationAvailability.Available || intent is null)
            throw new InvalidDataException("The reviewed installer plan has no valid binding.");
        using var lease = await ResourceLock.AcquireManyAsync(["recovery-state"],
            cancellationToken).ConfigureAwait(false);
        var (current, _, _, inventorySha256) = await BuildCurrentPlanAsync(distroId,
            intent.PlanId, intent.TargetId, intent.AllocationId,
            requestedLinuxBytes, verifiedIso, stagingDirectory,
            expectedPlan.GenerationId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(inventorySha256, expectedStagedInventorySha256,
                StringComparison.Ordinal) ||
            !JsonSerializer.SerializeToUtf8Bytes(current).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expectedPlan)))
            throw new StateChangedException();
        return current;
    }

    private async Task<(PreparationPlanV1 Plan, ReviewedInstallerSizingV1 Sizing,
        long StagedBytes, string StagedInventorySha256)> BuildCurrentPlanAsync(
        string distroId, Guid planId,
        Guid targetId, Guid allocationId, long requestedLinuxBytes,
        IsoAcquisitionResult verifiedIso, string stagingDirectory, Guid generation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (!verifiedIso.Sha256Verified ||
            verifiedIso.VerifiedSha256 is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit) || verifiedIso.SizeBytes <= 0 ||
            !Path.IsPathFullyQualified(verifiedIso.LocalPath))
            throw new InvalidDataException("The reviewed installer image has no verified local identity.");
        if (planId == Guid.Empty || targetId == Guid.Empty || allocationId == Guid.Empty ||
            requestedLinuxBytes <= 0 || string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new ArgumentException("A complete advisory plan, allocation and absolute staging path are required.");
        if (!registry.TryGet(distroId, out var plugin))
            throw new NotSupportedException("The selected installer provider is unavailable.");
        var declared = loader.LoadedDistros.SingleOrDefault(d => d.IsAvailable &&
            string.Equals(d.Id, distroId, StringComparison.Ordinal) &&
            d.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal));
        if (declared is null)
            throw new NotSupportedException("The selected provider does not support direct installation.");
        ReviewedInstallationProviderSupport.RequirePlanning(plugin);
        if (requestedLinuxBytes < Math.Max(20 * GiB,
                plugin.Metadata.MinimumRequirements.MinDiskBytes))
            throw new InvalidOperationException("The selected Linux root size is below the provider minimum.");

        var advisory = await ReadPlanAsync(planId, distroId, requestedLinuxBytes,
            cancellationToken).ConfigureAwait(false);
        await RequireCurrentPreflightAsync(advisory, cancellationToken).ConfigureAwait(false);

        var target = await targets.ResolveAsync(targetId, planId,
            advisory.ObservedStateFingerprint, cancellationToken).ConfigureAwait(false);
        var allocation = await allocations.ResolveAsync(allocationId, planId, targetId,
            cancellationToken).ConfigureAwait(false);
        RequireAllocation(target, allocation);
        var reviewedMedia = await media.AcquireAndInspectAsync(distroId, verifiedIso,
            cancellationToken).ConfigureAwait(false);
        if (reviewedMedia.DistroId != distroId ||
            !string.Equals(reviewedMedia.ProviderRevisionSha256,
                advisory.ProviderRevisionFingerprint, StringComparison.Ordinal) ||
            !string.Equals(advisory.ObservedStateFingerprint,
                MigrationStateRevision.Bind(advisory.ObservedMachineFingerprint,
                    reviewedMedia.ProviderRevisionSha256, distroId), StringComparison.Ordinal) ||
            !string.Equals(reviewedMedia.IsoSha256, verifiedIso.VerifiedSha256,
                StringComparison.OrdinalIgnoreCase) || reviewedMedia.IsoBytes != verifiedIso.SizeBytes ||
            reviewedMedia.Measurements.FullIsoBytes != (ulong)reviewedMedia.IsoBytes)
            throw new StateChangedException();

        var (manifest, _) = await UsbPreparation.ReadStagedManifestAsync(stagingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (manifest.DistroId != distroId || manifest.Hardware is null ||
            manifest.Hardware.InstallMode != "dual-boot" ||
            manifest.Hardware.TargetDiskGuid != target.Disk.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes <= 0 ||
            (ulong)manifest.Hardware.TargetDiskBytes != target.Disk.SizeBytes ||
            manifest.Hardware.LinuxPartitionSizeGb <= 0 ||
            checked((long)manifest.Hardware.LinuxPartitionSizeGb * GiB) != requestedLinuxBytes)
            throw new InvalidDataException("The staged installer differs from the selected distribution, disk or Linux allocation.");
        var stagedContent = ReviewedMediaInspector.InspectStagedContent(
            stagingDirectory, cancellationToken);
        var stagedBytes = stagedContent.TotalBytes;
        var bootSpec = ReviewedInstallationProviderSupport.ReviewedBootSpec(plugin) with
        { IsoSha256 = reviewedMedia.IsoSha256 };
        var sizing = ReviewedMediaInspector.PlanStorageSizes(bootSpec,
            requestedLinuxBytes, reviewedMedia.Measurements, stagedBytes);
        if (sizing.PayloadMinimumBytes <= 0 ||
            sizing.TotalShrinkBytes <= 0 || sizing.IsoMinimumBytes < 0)
            throw new NotSupportedException("The reviewed installer storage layout is unsupported.");

        // The allocation registry compares two current layout observations to its
        // durable digest. Bind the ESP and complete GPT inventory to those same facts.
        var first = ObserveLayout(target, allocation);
        var second = ObserveLayout(target, allocation);
        if (first.WindowsEsp != second.WindowsEsp ||
            !first.Partitions.SequenceEqual(second.Partitions))
            throw new StateChangedException();
        var spaceObservation = PreparationSpacePlanning.Plan(new(
            checked((ulong)allocation.NewFreeOffsetBytes),
            checked((ulong)allocation.PlannedShrinkBytes),
            checked((ulong)requestedLinuxBytes),
            checked((ulong)sizing.PayloadMinimumBytes),
            checked((ulong)sizing.IsoMinimumBytes),
            target.Disk.PhysicalSectorSize,
            PreCreateRoot: bootSpec.PreCreateRootPartition));
        if (spaceObservation.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException($"The selected shrink cannot hold the reviewed installer layout: {spaceObservation.Code}.");
        var plan = new PreparationPlanV1(1, generation, target.Disk,
            second.WindowsEsp, second.Partitions.Select(p => p.PartitionGuid).ToImmutableArray(),
            spaceObservation.Value)
        {
            ReviewedIntent = new ReviewedPreparationIntentV1(planId, targetId,
                allocationId, reviewedMedia.IsoSha256,
                reviewedMedia.ProviderRevisionSha256, reviewedMedia.Measurements,
                target, second.Partitions, checked((ulong)allocation.PlannedShrinkBytes),
                checked((ulong)allocation.ResultingPartitionBytes),
                checked((ulong)allocation.NewFreeOffsetBytes),
                checked((ulong)allocation.PlannedShrinkBytes))
            { StagedInventorySha256 = stagedContent.InventorySha256 },
        };
        if (PreparedLayoutRules.VerifyReviewedIntent(plan).Availability !=
            ObservationAvailability.Available)
            throw new InvalidDataException("The reviewed storage intent failed its structural checks.");

        // Reopen all durable identities and current facts once more immediately
        // before storing an evidence-only checkpoint. The later writer repeats them.
        var latest = await ReadPlanAsync(planId, distroId, requestedLinuxBytes,
            cancellationToken).ConfigureAwait(false);
        if (!SameAdvisory(advisory, latest) ||
            await targets.ResolveAsync(targetId, planId,
                advisory.ObservedStateFingerprint, cancellationToken).ConfigureAwait(false) != target ||
            !SameAllocation(await allocations.ResolveAsync(allocationId, planId, targetId,
                cancellationToken).ConfigureAwait(false), allocation))
            throw new StateChangedException();
        await RequireCurrentPreflightAsync(advisory, cancellationToken).ConfigureAwait(false);
        var final = ObserveLayout(target, allocation);
        var finalStagedContent = ReviewedMediaInspector.InspectStagedContent(
            stagingDirectory, cancellationToken);
        if (final.WindowsEsp != second.WindowsEsp ||
            !final.Partitions.SequenceEqual(second.Partitions) ||
            finalStagedContent != stagedContent)
            throw new StateChangedException();
        return (plan, sizing, stagedBytes, stagedContent.InventorySha256);
    }

    private async Task<MigrationPlanResultV2> ReadPlanAsync(Guid planId, string distroId,
        long linuxBytes, CancellationToken cancellationToken)
    {
        var plan = await journal.ReadAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The advisory plan is unavailable.");
        if (plan.DistroId != distroId || plan.RequiredBytes <= 0 ||
            linuxBytes < plan.RequiredBytes || plan.BlockerCodes.Count != 0 ||
            plan.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            plan.Executable || plan.Disposition != "AdvisoryOnly")
            throw new InvalidOperationException("The advisory plan cannot bind a reviewed installer layout.");
        return plan;
    }

    private async Task RequireCurrentPreflightAsync(MigrationPlanResultV2 plan,
        CancellationToken cancellationToken)
    {
        var report = await preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!ObservedStateRevision.Matches(report, plan.ObservedMachineFingerprint) ||
            !report.IsUefi || report.BitLocker != BitLockerState.NotEncrypted ||
            report.Findings.Any(f => f.Severity == FindingSeverity.Blocker))
            throw new StateChangedException();
    }

    private static bool SameAdvisory(MigrationPlanResultV2 before, MigrationPlanResultV2 after) =>
        before.PlanId == after.PlanId && before.DistroId == after.DistroId &&
        before.RequiredBytes == after.RequiredBytes &&
        before.ObservedAvailableBytes == after.ObservedAvailableBytes &&
        before.CapacityState == after.CapacityState &&
        before.BlockerCodes.SequenceEqual(after.BlockerCodes) &&
        before.ObservedStateFingerprint == after.ObservedStateFingerprint &&
        before.ObservedMachineFingerprint == after.ObservedMachineFingerprint &&
        before.ProviderRevisionFingerprint == after.ProviderRevisionFingerprint &&
        before.Executable == after.Executable && before.Disposition == after.Disposition;

    private static bool SameAllocation(MigrationAllocationResult before,
        MigrationAllocationResult after) =>
        before.PlanId == after.PlanId && before.TargetId == after.TargetId &&
        before.AllocationId == after.AllocationId && before.Scope == after.Scope &&
        before.RequestedShrinkBytes == after.RequestedShrinkBytes &&
        before.PlannedShrinkBytes == after.PlannedShrinkBytes &&
        before.CurrentPartitionBytes == after.CurrentPartitionBytes &&
        before.ResultingPartitionBytes == after.ResultingPartitionBytes &&
        before.NewFreeOffsetBytes == after.NewFreeOffsetBytes &&
        before.ObservedLayoutRevision == after.ObservedLayoutRevision &&
        before.CapacityState == after.CapacityState &&
        before.WarningCodes.SequenceEqual(after.WarningCodes) &&
        before.Executable == after.Executable;

    private static void RequireAllocation(CanonicalVolumeIdentityV1 target,
        MigrationAllocationResult allocation)
    {
        if (allocation.Scope != "WindowsBootNtfsShrinkIntentV1" ||
            allocation.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            allocation.Executable || allocation.PlannedShrinkBytes <= 0 ||
            allocation.CurrentPartitionBytes != checked((long)target.SizeBytes) ||
            allocation.ResultingPartitionBytes <= 0 ||
            allocation.ResultingPartitionBytes + allocation.PlannedShrinkBytes !=
                allocation.CurrentPartitionBytes ||
            allocation.NewFreeOffsetBytes != checked((long)target.OffsetBytes +
                allocation.ResultingPartitionBytes) ||
            allocation.ObservedLayoutRevision is not { Length: 64 })
            throw new InvalidDataException("The exact advisory shrink no longer matches the Windows volume.");
    }

    private ReviewedObservedLayout ObserveLayout(CanonicalVolumeIdentityV1 target,
        MigrationAllocationResult allocation)
    {
        var observed = allocationSource.Observe(target) ?? throw new StateChangedException();
        if (!string.Equals(FileMigrationAllocationRegistry.LayoutRevision(observed),
                allocation.ObservedLayoutRevision, StringComparison.Ordinal))
            throw new StateChangedException();
        var read = storage.ReadIdentitySnapshot();
        if (read.Availability != ObservationAvailability.Available ||
            CanonicalRecoveryIdentity.Bind(read, target.VolumeGuid) is not
                { Availability: ObservationAvailability.Available } source ||
            source.Value != target)
            throw new StateChangedException();
        var snapshot = read.Value;
        var disks = snapshot.Disks.Where(d =>
            d.GptGuid.Availability == ObservationAvailability.Available &&
            d.GptGuid.Value == target.Disk.GptDiskGuid &&
            d.Number.Availability == ObservationAvailability.Available &&
            d.UniqueId.Availability == ObservationAvailability.Available &&
            d.UniqueId.Value == target.Disk.UniqueId &&
            d.Size.Availability == ObservationAvailability.Available &&
            d.Size.Value == target.Disk.SizeBytes).ToArray();
        if (disks.Length != 1 || snapshot.Partitions.Any(p =>
                p.DiskNumber.Availability != ObservationAvailability.Available))
            throw new StateChangedException();
        var diskNumber = disks[0].Number.Value;
        var onDisk = snapshot.Partitions.Where(p => p.DiskNumber.Value == diskNumber)
            .ToArray();
        if (onDisk.Length != observed.Partitions.Count)
            throw new StateChangedException();
        var partitions = ImmutableArray.CreateBuilder<PreparedGptPartitionV1>(onDisk.Length);
        foreach (var p in onDisk)
        {
            if (p.PartitionGuid.Availability != ObservationAvailability.Available ||
                p.GptType.Availability != ObservationAvailability.Available ||
                p.Offset.Availability != ObservationAvailability.Available ||
                p.Size.Availability != ObservationAvailability.Available)
                throw new StateChangedException();
            partitions.Add(new(target.Disk, p.PartitionGuid.Value, p.GptType.Value,
                p.Offset.Value, p.Size.Value));
        }
        var ordered = partitions.OrderBy(p => p.OffsetBytes).ToImmutableArray();
        if (!ordered.Select(p => new AllocationPartitionIdentity(p.PartitionGuid,
                p.PartitionType, p.OffsetBytes, p.SizeBytes))
                .SequenceEqual(observed.Partitions.OrderBy(p => p.OffsetBytes)))
            throw new StateChangedException();
        var candidates = onDisk.Where(p =>
            p.GptType.Value == PreparationSpacePlanning.EspType &&
            p.IsSystem.Availability == ObservationAvailability.Available && p.IsSystem.Value)
            .ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("The current Windows system ESP is unavailable or ambiguous.");
        var espVolumes = snapshot.Volumes.Where(v =>
            v.PartitionGuid.Availability == ObservationAvailability.Available &&
            v.PartitionGuid.Value == candidates[0].PartitionGuid.Value &&
            v.VolumeGuid.Availability == ObservationAvailability.Available)
            .ToArray();
        if (espVolumes.Length != 1 ||
            CanonicalRecoveryIdentity.Bind(read, espVolumes[0].VolumeGuid.Value) is not
                { Availability: ObservationAvailability.Available } esp ||
            esp.Value.Disk != target.Disk ||
            esp.Value.PartitionType != PreparationSpacePlanning.EspType ||
            !string.Equals(esp.Value.FileSystem, "FAT32", StringComparison.Ordinal))
            throw new InvalidOperationException("The current Windows system ESP lacks exact FAT32 identity.");
        return new(esp.Value, ordered);
    }

    private sealed record ReviewedObservedLayout(CanonicalVolumeIdentityV1 WindowsEsp,
        ImmutableArray<PreparedGptPartitionV1> Partitions);
}
