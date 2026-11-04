using System.Text.Json;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

// The checkpoint is mandatory; generic exception constructors would discard
// the only safe way to inspect an interrupted native storage transition.
#pragma warning disable CA1032
internal sealed class ReviewedStoragePreparationException(
    string message, PreparationCheckpointReference latestCheckpoint, Exception inner)
    : InvalidOperationException(message, inner)
{
    internal PreparationCheckpointReference LatestCheckpoint { get; } = latestCheckpoint;
}
#pragma warning restore CA1032

public sealed partial class DirectInstallService
{
    // Read-only media sizing for a reviewed plan. It uses the existing pinned
    // ISO9660 reader, never a mounted drive letter. Debian's hd-media pair is
    // measured from its separate local SHA-pinned downloads.
    internal static ReviewedMediaMeasurementsV1 MeasureReviewedMedia(
        string pinnedIsoPath, InstallerBootSpec bootSpec,
        string? pinnedKernelPath = null, string? pinnedInitrdPath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bootSpec);
        ValidateExternalInstallerMedia(bootSpec);
        if (!Path.IsPathFullyQualified(pinnedIsoPath))
            throw new InvalidDataException("The reviewed installer image path is not absolute.");
        var isoFile = new FileInfo(pinnedIsoPath);
        if (!isoFile.Exists || isoFile.Length <= 0 ||
            (isoFile.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The pinned reviewed installer image is unavailable or redirected.");
        var isoBytes = isoFile.Length;
        using var iso = new ReviewedPinnedIsoSource(pinnedIsoPath, isoBytes,
            bootSpec.IsoSha256 ?? "", ct);

        long kernelBytes;
        long initrdBytes;
        if (bootSpec.KernelUrl is not null || bootSpec.InitrdUrl is not null)
        {
            kernelBytes = MeasureExternal(pinnedKernelPath, bootSpec.KernelSha256, ct);
            initrdBytes = MeasureExternal(pinnedInitrdPath, bootSpec.InitrdSha256, ct);
        }
        else
        {
            if (pinnedKernelPath is not null || pinnedInitrdPath is not null)
                throw new InvalidDataException("Unexpected external installer media were supplied.");
            var kernel = iso.FirstAvailable(bootSpec.KernelIsoPaths) ??
                throw new InvalidDataException("The pinned ISO has no declared installer kernel.");
            var initrd = iso.FirstAvailable(bootSpec.InitrdIsoPaths) ??
                throw new InvalidDataException("The pinned ISO has no declared installer initrd.");
            kernelBytes = iso.HashMember(kernel, ct: ct).SizeBytes;
            initrdBytes = iso.HashMember(initrd, ct: ct).SizeBytes;
        }

        if (bootSpec.ExtraIsoFiles.Count > 1)
            throw new NotSupportedException("The reviewed seed writer supports one declared stage2 file.");
        long stage2Bytes = 0;
        foreach (var extra in bootSpec.ExtraIsoFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (extra is null || string.IsNullOrWhiteSpace(extra.IsoRelativePath))
                throw new InvalidDataException("The reviewed installer stage2 declaration is invalid.");
            var present = iso.FirstAvailable([extra.IsoRelativePath]);
            if (present is null)
            {
                if (extra.Required)
                    throw new InvalidDataException("A required installer stage2 file is absent from the pinned ISO.");
                continue;
            }
            stage2Bytes = checked(stage2Bytes + iso.HashMember(present, ct: ct).SizeBytes);
        }
        iso.VerifyPin(ct);
        return new ReviewedMediaMeasurementsV1(checked((ulong)isoBytes),
            checked((ulong)kernelBytes), checked((ulong)initrdBytes),
            checked((ulong)stage2Bytes));
    }

    private static long MeasureExternal(string? path, string? expectedSha256,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            expectedSha256 is not { Length: 64 } ||
            !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The reviewed external installer media have no exact file and pin.");
        var sourceFile = new FileInfo(path);
        if (!sourceFile.Exists ||
            (sourceFile.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The pinned external installer file is unavailable or redirected.");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (source.Length <= 0)
            throw new InvalidDataException("The reviewed external installer media are empty.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = source.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()),
                expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed external installer media failed their SHA-256 pin.");
        return source.Length;
    }

    // Internal native-storage sequence for a caller that can re-resolve the
    // durable advisory plan and prove the entire storage recovery footprint.
    // Today's production footprint has missing proofs, so the default gate
    // rejects this before Resize. Public PrepareAsync stays closed as well.
    internal static async Task<PreparationCheckpointReference> PrepareReviewedStorageAsync(
        PreparedLayoutStore checkpoints, ReviewedPartitionCreationService reviewed,
        PartitionResizeService resizer, PreparationCheckpointReference plannedReference,
        Func<CancellationToken, Task<PreparationPlanV1>> resolveCurrentPlan,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string pinnedIsoPath,
        Func<PreparationPlanV1, CancellationToken,
            Observation<PreparedLayoutRecoveryFootprintV1>>? observeRecovery = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(resizer);
        ArgumentNullException.ThrowIfNull(plannedReference);
        ArgumentNullException.ThrowIfNull(resolveCurrentPlan);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (!Path.IsPathFullyQualified(pinnedIsoPath))
            throw new InvalidDataException("The reviewed installer image path is not absolute.");

        var checkpoint = checkpoints.Reopen(plannedReference);
        var plan = checkpoint.Plan;
        if (checkpoint.State != PreparationState.Planned ||
            checkpoint.ShrinkReceipt is not null || checkpoint.StorageOwnership is not null ||
            !checkpoint.CreationReceipts.IsEmpty)
            throw new InvalidOperationException("The reviewed installer storage checkpoint is not an untouched plan.");
        var review = ReviewPreparedIntent(plan, bootSpec, requestedLinuxBytes);
        if (review.Availability != ObservationAvailability.Available)
            throw new InvalidDataException($"The reviewed installer plan is invalid: {review.Code}.");

        var expectedPlanBytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        async Task RequireCurrentPlanAsync()
        {
            var current = await resolveCurrentPlan(ct).ConfigureAwait(false);
            if (current is null || !JsonSerializer.SerializeToUtf8Bytes(current)
                    .AsSpan().SequenceEqual(expectedPlanBytes))
                throw new InvalidOperationException("The durable installer plan, target, allocation, artifact or provider changed.");
        }

        // Reopen the ISO from its path and hash its complete pinned bytes before
        // the recovery observation. Later seed copying repeats this pin.
        await RequireCurrentPlanAsync().ConfigureAwait(false);
        var media = new FileInfo(pinnedIsoPath);
        if (!media.Exists || (media.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (ulong)media.Length != plan.ReviewedIntent!.Media.FullIsoBytes)
            throw new InvalidDataException("The reviewed installer image differs from its measured media intent.");
        VerifySelectedIso(pinnedIsoPath, plan.ReviewedIntent.ArtifactSha256, ct);
        await RequireCurrentPlanAsync().ConfigureAwait(false);

        observeRecovery ??= static (current, _) =>
            PreparedLayoutRecoveryFootprint.Derive(current);
        var expectedFootprint = PreparedLayoutRecoveryFootprint.Derive(plan);
        if (expectedFootprint.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The reviewed storage recovery footprint is invalid.");
        void RequireRecovery()
        {
            ct.ThrowIfCancellationRequested();
            // A caller-supplied observation cannot erase proof gaps derived
            // from the reviewed plan. In particular, a test or future host
            // adapter must not authorize native writes by returning an empty
            // MissingProofCodes array for the same mutation inventory.
            if (expectedFootprint.Value.MissingProofCodes.IsDefault ||
                !expectedFootprint.Value.MissingProofCodes.IsEmpty)
                throw new NotSupportedException("The exact reviewed storage recovery footprint is not authorized for native preparation.");
            var observed = observeRecovery(plan, ct);
            if (observed.Availability != ObservationAvailability.Available ||
                observed.Value is null ||
                observed.Value.GenerationId != plan.GenerationId ||
                observed.Value.Mutations.IsDefault ||
                !observed.Value.Mutations.SequenceEqual(expectedFootprint.Value.Mutations) ||
                observed.Value.MissingProofCodes.IsDefault ||
                !observed.Value.MissingProofCodes.SequenceEqual(
                    expectedFootprint.Value.MissingProofCodes))
                throw new NotSupportedException("The exact reviewed storage recovery footprint is not authorized for native preparation.");
        }

        RequireRecovery();
        await RequireCurrentPlanAsync().ConfigureAwait(false);
        if (checkpoints.Reopen(plannedReference).State != PreparationState.Planned)
            throw new InvalidOperationException("The reviewed installer checkpoint changed before native preparation.");
        RequireRecovery();
        ct.ThrowIfCancellationRequested();

        var latest = plannedReference;
        try
        {
            var shrink = await resizer.ShrinkReviewedAsync(plan, ct).ConfigureAwait(false);
            // No cancellation check here: once Resize returned, persist its
            // independent readback even if the caller has cancelled.
            try
            {
                if (PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability !=
                    ObservationAvailability.Available)
                    throw new InvalidOperationException("The reviewed Windows resize has no NTFS filesystem receipt.");
                latest = reviewed.PersistReviewedShrink(latest, shrink);
            }
            catch (Exception receiptFailure)
            {
                // A successful native return still leaves a changed partition
                // when its exact receipt cannot be saved. Pin ambiguity before
                // exposing another plan or attempting any partition creation.
                try
                {
                    latest = checkpoints.PersistAndReopen(checkpoints.Reopen(latest) with
                        { State = PreparationState.AmbiguousLeftover });
                }
#pragma warning disable CA1031
                catch (Exception ambiguityFailure)
#pragma warning restore CA1031
                {
                    throw new ReviewedStoragePreparationException(
                        "The reviewed Windows resize changed the disk, and its ambiguous checkpoint could not be saved. Inspect the exact disk before retrying.",
                        latest, new AggregateException(receiptFailure, ambiguityFailure));
                }
                throw new ReviewedStoragePreparationException(
                    "The reviewed Windows resize changed the disk, but its exact receipt could not be saved. Inspect the ambiguous checkpoint before retrying.",
                    latest, receiptFailure);
            }
            foreach (var _ in plan.Space.Allocations.Where(a => a.CreateInWindows))
            {
                ct.ThrowIfCancellationRequested();
                latest = reviewed.CreateAndFormatNext(latest, shrink, ct);
            }
            var root = plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot);
            if (root.CreateInWindows)
            {
                var owned = reviewed.ObserveOwnedRootHandoff(latest, ct);
                if (owned.Checkpoint != latest || owned.TargetDisk != plan.TargetDisk ||
                    owned.GenerationId != plan.GenerationId ||
                    owned.LinuxRoot.OffsetBytes != root.OffsetBytes ||
                    owned.LinuxRoot.SizeBytes != root.SizeBytes)
                    throw new InvalidOperationException("The completed reviewed owned-root handoff changed.");
            }
            else
            {
                var handoff = reviewed.ObserveInstallerHandoff(latest, ct);
                if (handoff.Checkpoint != latest || handoff.TargetDisk != plan.TargetDisk ||
                    handoff.GenerationId != plan.GenerationId)
                    throw new InvalidOperationException("The completed reviewed storage handoff changed.");
            }
            return latest;
        }
        catch (ReviewedShrinkMutationException uncertain)
        {
            throw PersistUncertainReviewedShrink(checkpoints, reviewed, latest, uncertain);
        }
        catch (AmbiguousPartitionCreationException)
        {
            throw;
        }
        catch (ReviewedStoragePreparationException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ReviewedStoragePreparationException(
                "Reviewed storage preparation stopped. The latest pinned checkpoint must be inspected before any retry.",
                latest, error);
        }
    }

    // Recovery classification is independent of native write authorization.
    // Keep it testable while the reviewed production writer remains closed.
    internal static ReviewedStoragePreparationException PersistUncertainReviewedShrink(
        PreparedLayoutStore checkpoints, ReviewedPartitionCreationService reviewed,
        PreparationCheckpointReference latest, ReviewedShrinkMutationException uncertain)
    {
        try
        {
            latest = uncertain.VerifiedReceipt is { } exact
                ? reviewed.PersistReviewedShrink(latest, exact)
                : checkpoints.PersistAndReopen(checkpoints.Reopen(latest) with
                    { State = PreparationState.AmbiguousLeftover });
        }
        // If the exact receipt cannot be persisted, pin ambiguity instead.
        // A failed artifact write must never be presented as a safe retry.
#pragma warning disable CA1031
        catch (Exception persistenceFailure)
#pragma warning restore CA1031
        {
            if (uncertain.VerifiedReceipt is not null)
            {
                try
                {
                    latest = checkpoints.PersistAndReopen(checkpoints.Reopen(latest) with
                        { State = PreparationState.AmbiguousLeftover });
                }
#pragma warning disable CA1031
                catch (Exception ambiguityFailure)
#pragma warning restore CA1031
                {
                    return new ReviewedStoragePreparationException(
                        "The reviewed Windows resize stopped, and its ambiguous checkpoint could not be saved. Inspect the exact disk before retrying.",
                        latest, new AggregateException(uncertain, persistenceFailure, ambiguityFailure));
                }
                return new ReviewedStoragePreparationException(
                    "The reviewed Windows resize stopped. Its exact receipt could not be saved; inspect the ambiguous checkpoint before retrying.",
                    latest, new AggregateException(uncertain, persistenceFailure));
            }
            return new ReviewedStoragePreparationException(
                "The reviewed Windows resize stopped, and its ambiguous checkpoint could not be saved. Inspect the exact disk before retrying.",
                latest, new AggregateException(uncertain, persistenceFailure));
        }
        return new ReviewedStoragePreparationException(
            "The reviewed Windows resize stopped. Inspect the latest pinned checkpoint before any retry.",
            latest, uncertain);
    }
}
