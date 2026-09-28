using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class CommunityRecoveryBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igloo-community-recovery-test-" + Guid.NewGuid().ToString("D"));

    [Fact]
    public void ExactCaptureIsFlushedReopenedWithANewStoreAndRevalidatedBeforeMutation()
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var events = new List<string>();
        var reader = new FakeCapture(() => { events.Add("capture"); return snapshot; });
        var store = new Store((id, bytes) => { events.Add("persist"); new CommunityRecoveryArtifactStore(_root).PersistNew(id, bytes); },
            id => { events.Add("reopen"); return new CommunityRecoveryArtifactStore(_root).Reopen(id); });
        var receipt = new CommunityRecoveryBoundary(reader, store).Execute(Request(snapshot), () => events.Add("mutation"));
        Assert.Equal(new[] { "capture", "persist", "reopen", "capture", "mutation" }, events);
        Assert.Equal(snapshot.CanonicalHash, receipt.SnapshotCanonicalHash);
        Assert.Equal(64, receipt.ArtifactSha256.Length);
        Assert.Single(Directory.GetFiles(_root));
        Assert.NotEmpty(new CommunityRecoveryArtifactStore(_root).Reopen(receipt.ArtifactId));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unsupported")]
    [InlineData("hash")]
    public void NonExactSnapshotNeverReachesPersistenceOrMutation(string failure)
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var invalid = failure switch
        {
            "partial" => RecoverySnapshotSerialization.Seal(snapshot with { IndependentReadback = Observations.Failure<bool>(ObservationAvailability.Unavailable, "test") }),
            "unsupported" => RecoverySnapshotSerialization.Seal(snapshot with { SchemaVersion = 2 }),
            _ => snapshot with { CanonicalHash = new string('0', 64) },
        };
        var writes = 0; var mutations = 0;
        var boundary = new CommunityRecoveryBoundary(new FakeCapture(() => invalid), new Store((_, _) => writes++, _ => throw new IOException()));
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.Execute(Request(snapshot), () => mutations++));
        Assert.Equal(CommunityRecoveryFailure.SnapshotNotExact, error.Reason);
        Assert.Equal(0, writes); Assert.Equal(0, mutations);
    }

    [Fact]
    public void CorruptedArtifactIsRejectedBeforeRecaptureAndMutation()
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        byte[] saved = []; var captures = 0; var mutations = 0;
        var store = new Store((_, bytes) => saved = bytes.ToArray(), _ => { saved[^2] ^= 1; return saved; });
        var boundary = new CommunityRecoveryBoundary(new FakeCapture(() => { captures++; return snapshot; }), store);
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.Execute(Request(snapshot), () => mutations++));
        Assert.Equal(CommunityRecoveryFailure.ArtifactInvalid, error.Reason);
        Assert.Equal(1, captures); Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistOrReopenFailureNeverInvokesMutation(bool reopenFailure)
    {
        var snapshot = CommunityRecoveryFixture.Exact(); var mutations = 0;
        var store = new Store((_, _) => { if (!reopenFailure) throw new IOException("disk full"); }, _ => throw new IOException("reopen failed"));
        var boundary = new CommunityRecoveryBoundary(new FakeCapture(() => snapshot), store);
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.Execute(Request(snapshot), () => mutations++));
        Assert.Equal(reopenFailure ? CommunityRecoveryFailure.ReopenFailed : CommunityRecoveryFailure.PersistenceFailed, error.Reason);
        Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrUnavailableMachineAfterCaptureNeverInvokesMutation(bool unavailable)
    {
        var snapshot = CommunityRecoveryFixture.Exact(); var calls = 0; var mutations = 0;
        var changed = RecoverySnapshotSerialization.Seal(snapshot with
        {
            Rtc = new(Observations.Available(true), Observations.Available(new RegistryValueV1(11, [1, 0, 0, 0, 0, 0, 0, 0]))),
        });
        var reader = new FakeCapture(() => ++calls == 1 ? snapshot : unavailable ? throw new IOException("observation failed") : changed);
        var boundary = new CommunityRecoveryBoundary(reader, new CommunityRecoveryArtifactStore(_root));
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.Execute(Request(snapshot), () => mutations++));
        Assert.Equal(CommunityRecoveryFailure.RevalidationFailed, error.Reason);
        Assert.Equal(0, mutations);
        Assert.Single(Directory.GetFiles(_root)); // Evidence remains; no automatic rollback/cleanup.
    }

    [Fact]
    public void SnapshotOfAnotherTargetCannotSatisfyTheDeclaration()
    {
        var snapshot = CommunityRecoveryFixture.Exact(); var mutations = 0;
        var request = Observations.Available(new CommunityBootRecoveryRequest(snapshot.Scope, Guid.NewGuid()));
        var boundary = new CommunityRecoveryBoundary(new FakeCapture(() => snapshot), new CommunityRecoveryArtifactStore(_root));
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.Execute(request, () => mutations++));
        Assert.Equal(CommunityRecoveryFailure.ScopeMismatch, error.Reason);
        Assert.Equal(0, mutations); Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CancellationAfterRecapturePreventsMutation()
    {
        var snapshot = CommunityRecoveryFixture.Exact(); var calls = 0; var mutations = 0;
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeCapture(() => { if (++calls == 2) cancellation.Cancel(); return snapshot; });
        var boundary = new CommunityRecoveryBoundary(reader, new CommunityRecoveryArtifactStore(_root));
        Assert.Throws<OperationCanceledException>(() => boundary.Execute(Request(snapshot), () => mutations++, cancellation.Token));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public void MutationFailureIsNotRetriedOrRelabelledAsObservationFailure()
    {
        var snapshot = CommunityRecoveryFixture.Exact(); var mutations = 0;
        var failure = new IOException("mutation failed");
        var boundary = new CommunityRecoveryBoundary(new FakeCapture(() => snapshot), new CommunityRecoveryArtifactStore(_root));
        var actual = Assert.Throws<IOException>(() => boundary.Execute(Request(snapshot), () => { mutations++; throw failure; }));
        Assert.Same(failure, actual); Assert.Equal(1, mutations); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void ExistingDirectInstallRegistrationStopsAtUnresolvedScopeBeforeItsMutationStage()
    {
        var service = new DirectInstallService(null!, NullLogger<DirectInstallService>.Instance);
        var mutations = 0;
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => service.RegisterPreparedBootEntry(() => mutations++));
        Assert.Equal(CommunityRecoveryFailure.ScopeUnresolved, error.Reason);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public void ArtifactStoreRefusesOverwriteAndKeepsOriginalBytes()
    {
        var store = new CommunityRecoveryArtifactStore(_root); var id = Guid.NewGuid();
        store.PersistNew(id, [1, 2, 3]);
        Assert.Throws<IOException>(() => store.PersistNew(id, [4, 5, 6]));
        Assert.Equal(new byte[] { 1, 2, 3 }, new CommunityRecoveryArtifactStore(_root).Reopen(id));
    }

    private static Observation<CommunityBootRecoveryRequest> Request(RecoverySnapshotV1 snapshot) =>
        Observations.Available(new CommunityBootRecoveryRequest(snapshot.Scope, snapshot.Binding.Value.TargetVolume.VolumeGuid));
    private sealed class FakeCapture(Func<RecoverySnapshotV1> read) : IRecoverySnapshotCapture
    {
        public RecoverySnapshotV1 Capture(RecoveryScopeV1 scope, Guid canonicalTargetVolume) => read();
    }
    private sealed class Store(Action<Guid, byte[]> persist, Func<Guid, byte[]> reopen) : ICommunityRecoveryArtifactStore
    {
        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact) => persist(artifactId, artifact.ToArray());
        public byte[] Reopen(Guid artifactId) => reopen(artifactId);
    }

    public void Dispose()
    {
        // This instance owns one unique test directory; no recursive deletion or reparse fixtures.
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.GetFiles(_root)) File.Delete(file);
        Directory.Delete(_root, recursive: false);
    }
}
