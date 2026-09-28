using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityRecovery;

public sealed record CommunityBootRecoveryRequest(RecoveryScopeV1 Scope, Guid TargetVolume);
public sealed record CommunityRecoveryReceipt(Guid ArtifactId, string ArtifactSha256, string SnapshotCanonicalHash);
public enum CommunityRecoveryFailure
{
    ScopeUnresolved, CaptureFailed, SnapshotNotExact, ScopeMismatch, PersistenceFailed,
    ReopenFailed, ArtifactInvalid, RevalidationFailed,
}

public sealed class CommunityRecoveryBoundaryException : InvalidOperationException
{
    public CommunityRecoveryBoundaryException() : this(CommunityRecoveryFailure.ScopeUnresolved) { }
    public CommunityRecoveryBoundaryException(string? message) : base(message) { }
    public CommunityRecoveryBoundaryException(string? message, Exception? innerException) : base(message, innerException) { }
    public CommunityRecoveryBoundaryException(CommunityRecoveryFailure reason, Exception? inner = null)
        : base($"Boot registration was blocked by recovery verification ({reason}). No boot changes were started.", inner)
        => Reason = reason;

    // Standard exception constructors never imply a resolved or authorized mutation scope.
    public CommunityRecoveryFailure Reason { get; } = CommunityRecoveryFailure.ScopeUnresolved;
}

// Orchestration only: uses the shared snapshot serializer/assessment/comparison without overriding them.
public sealed class CommunityRecoveryBoundary(IRecoverySnapshotCapture capture, ICommunityRecoveryArtifactStore store)
{
    private readonly IRecoverySnapshotCapture _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly ICommunityRecoveryArtifactStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private static readonly JsonSerializerOptions EnvelopeOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    // No invented volume GUID, Boot0000 assumption or generic scope substituted for the current dynamic installer.
    public static Observation<CommunityBootRecoveryRequest> DirectInstallDeclaration =>
        Observations.Failure<CommunityBootRecoveryRequest>(ObservationAvailability.Unavailable, "DirectInstallMutationFootprintUnresolved");

    // No arbitrary caller-supplied scope can upgrade the candidate plan. Until the Windows BCD
    // provider footprint is supported, Declaration fails before capture and the checked native
    // program is unreachable. The production caller is CommunityBootRegistrationExecutor.
    internal CommunityRecoveryReceipt ExecutePlan(CommunityBootRegistrationPlan plan,
        System.Collections.Immutable.ImmutableArray<BootRegistrationMutation> proposedMutations,
        Func<BootRegistrationEvidence> readCurrent, Action mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        plan.RequireExactMutations(proposedMutations);
        plan.RequireUnchanged(readCurrent());
        return Execute(plan.Declaration, () =>
        {
            plan.RequireExactMutations(proposedMutations);
            plan.RequireUnchanged(readCurrent());
            mutation();
        }, cancellationToken);
    }

    public CommunityRecoveryReceipt Execute(Observation<CommunityBootRecoveryRequest> declaration, Action mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        if (declaration.Availability != ObservationAvailability.Available)
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeUnresolved);
        var request = declaration.Value;
        if (request.TargetVolume == Guid.Empty || !request.Scope.IncludeRtc || !request.Scope.MutationFootprintResolved)
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeUnresolved);

        var snapshot = ReadSnapshot(request, CommunityRecoveryFailure.CaptureFailed);
        RequireExact(snapshot);
        if (!BoundToRequest(snapshot, request)) throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeMismatch);

        var snapshotBytes = RecoverySnapshotSerialization.Serialize(snapshot);
        var id = Guid.NewGuid();
        var envelope = new RecoveryArtifact(1, id, snapshotBytes.Length, Digest(snapshotBytes), snapshot.CanonicalHash, snapshotBytes);
        var artifact = JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeOptions);
        var expectedDigest = Digest(artifact); // Pinned before the store sees the bytes; not trusted from disk.
        cancellationToken.ThrowIfCancellationRequested();
        try { _store.PersistNew(id, artifact); }
        catch (Exception error) when (IsBoundaryError(error))
        { throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.PersistenceFailed, error); }

        byte[] reopenedBytes;
        try { reopenedBytes = _store.Reopen(id); }
        catch (Exception error) when (IsBoundaryError(error))
        { throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ReopenFailed, error); }
        RecoverySnapshotV1 reopened;
        try
        {
            if (Digest(reopenedBytes) != expectedDigest) throw new InvalidDataException("Artifact integrity mismatch.");
            var read = JsonSerializer.Deserialize<RecoveryArtifact>(reopenedBytes, EnvelopeOptions)
                ?? throw new InvalidDataException("Artifact missing.");
            if (read.SchemaVersion != 1 || read.ArtifactId != id || read.Snapshot.Length != read.SnapshotLength ||
                Digest(read.Snapshot) != read.SnapshotSha256 || read.SnapshotCanonicalHash != snapshot.CanonicalHash)
                throw new InvalidDataException("Artifact manifest mismatch.");
            reopened = RecoverySnapshotSerialization.Deserialize(read.Snapshot);
            RequireExact(reopened);
            if (reopened.CanonicalHash != read.SnapshotCanonicalHash || !BoundToRequest(reopened, request))
                throw new InvalidDataException("Snapshot binding mismatch.");
        }
        catch (Exception error) when (IsBoundaryError(error))
        { throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ArtifactInvalid, error); }

        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadSnapshot(request, CommunityRecoveryFailure.RevalidationFailed);
        if (!BoundToRequest(current, request) || RecoverySnapshotRules.Compare(reopened, current).Result != RecoverySnapshotMatch.ExactMatch)
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.RevalidationFailed);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep mutation exceptions outside all observation/persistence catches. Never retry or restore here.
        mutation();
        return new(id, expectedDigest, reopened.CanonicalHash);
    }

    private RecoverySnapshotV1 ReadSnapshot(CommunityBootRecoveryRequest request, CommunityRecoveryFailure failure)
    {
        try { return _capture.Capture(request.Scope, request.TargetVolume); }
        catch (Exception error) when (IsBoundaryError(error)) { throw new CommunityRecoveryBoundaryException(failure, error); }
    }

    private static void RequireExact(RecoverySnapshotV1 snapshot)
    {
        if (RecoverySnapshotRules.Assess(snapshot).Support != BootRecoverySupport.Exact)
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.SnapshotNotExact);
    }

    private static bool BoundToRequest(RecoverySnapshotV1 snapshot, CommunityBootRecoveryRequest request) =>
        snapshot.Binding.Availability == ObservationAvailability.Available && snapshot.Binding.Value.TargetVolume.VolumeGuid == request.TargetVolume &&
        JsonSerializer.Serialize(snapshot.Scope, EnvelopeOptions) == JsonSerializer.Serialize(request.Scope, EnvelopeOptions);
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsBoundaryError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or
        JsonException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException or OverflowException;
    private sealed record RecoveryArtifact(int SchemaVersion, Guid ArtifactId, int SnapshotLength, string SnapshotSha256,
        string SnapshotCanonicalHash, byte[] Snapshot);
}
