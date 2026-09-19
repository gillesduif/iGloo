using Igloo.Core.Execution;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class RecoverableExecutionTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "igloo-execution-" + Guid.NewGuid());
    private readonly ExecutionCorrelation _correlation = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "approved-hash", Guid.NewGuid(), Guid.NewGuid());
    private readonly StorageState _before = new(new("hardware-serial", Guid.NewGuid(), Guid.NewGuid(), 3),
        1048576, 100000000, "NTFS", "Windows");
    private SqliteExecutionJournal<AuthorizedMutation<StorageState>> Journal() => new(Path.Join(_directory, "execution.db"));

    [Theory]
    [InlineData(CrashPoint.None, MutationOutcome.AppliedAndVerified)]
    [InlineData(CrashPoint.BeforeMutation, MutationOutcome.NotApplied)]
    [InlineData(CrashPoint.DuringMutation, MutationOutcome.AmbiguousRecoveryRequired)]
    [InlineData(CrashPoint.AfterMutation, MutationOutcome.AppliedAndVerified)]
    [InlineData(CrashPoint.AfterVerification, MutationOutcome.AppliedAndVerified)]
    public async Task RestartInspectsIndependentState(CrashPoint crash, MutationOutcome expected)
    {
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        var journal = Journal();
        var machine = new FakeMachine<StorageState>(_before);
        var adapter = new FakeAdapter<StorageState>(machine, _before with { LengthBytes = 75000000 })
        {
            Crash = crash
        };
        // Reading through a reconstructed journal must not reacquire the writer's exclusive lock.
        adapter.BeforeApply = () => Assert.Equal(ExecutionRecordKind.Intent, Assert.Single(journal.Read(_correlation.OperationId)).Kind);
        var coordinator = new RecoveryCoordinator<StorageState>(journal, adapter);
        if (crash == CrashPoint.None)
            Assert.Equal(expected, await coordinator.RunAsync(_correlation, mutation));
        else
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(_correlation, mutation));
        var recovered = new RecoveryCoordinator<StorageState>(Journal(), new FakeAdapter<StorageState>(machine, _before));
        Assert.Equal(expected, await recovered.RecoverAsync(_correlation.OperationId));
        Assert.Equal(expected, await recovered.RunAsync(_correlation, mutation));
        Assert.Equal(crash == CrashPoint.BeforeMutation ? 0 : 1, machine.ApplyCount);
        var records = Journal().Read(_correlation.OperationId);
        Assert.Equal(ExecutionRecordKind.Intent, records[0].Kind);
        if (expected == MutationOutcome.AppliedAndVerified)
            Assert.Contains(records, r => r.Kind == ExecutionRecordKind.VerifiedReceipt && r.ObservationJson != null);
    }

    [Fact]
    public async Task ExclusiveJournalPreventsConcurrentApplyAndChangedAuthority()
    {
        var journal = Journal();
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        var machine = new FakeMachine<StorageState>(_before);
        var coordinator = new RecoveryCoordinator<StorageState>(journal, new FakeAdapter<StorageState>(machine, _before));
        using (journal.Acquire())
            await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(_correlation, mutation));
        Assert.Equal(0, machine.ApplyCount);
        await coordinator.RunAsync(_correlation, mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(_correlation,
            mutation with { After = _before with { LengthBytes = 40000000 } }));
        Assert.Equal(1, machine.ApplyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootRestorationRequiresExactIndependentReadback(bool partial)
    {
        var before = new BootState("fake-v1", "all-boot-state", "original");
        var adapter = new FakeBootAdapter(before);
        var snapshot = await adapter.CaptureAsync(default);
        await adapter.ApplyAsync(new(before, before with { CanonicalContent = "changed" }), default);
        adapter.PartialRestore = partial;
        var restoration = await adapter.RestoreAsync(snapshot, before with { CanonicalContent = "changed" }, default);
        Assert.Equal(partial ? BootRecoverySupport.Partial : BootRecoverySupport.Exact, restoration.Support);
        Assert.Equal(partial ? VerificationDisposition.Different : VerificationDisposition.Exact, restoration.Verification.Disposition);
    }

    [Fact]
    public async Task DiskOrdinalAloneCannotAuthorizeMutation()
    {
        var state = _before with { Target = new("", Guid.Empty, Guid.Empty, 1) };
        var coordinator = new RecoveryCoordinator<StorageState>(Journal(), new FakeAdapter<StorageState>(new(state), state));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RunAsync(_correlation, new(state, state with { LengthBytes = 1 })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryExceptionNeverProvesNoMutation(bool afterMutation)
    {
        var machine = new FakeMachine<StorageState>(_before);
        var adapter = new FakeAdapter<StorageState>(machine, _before) { ThrowOrdinary = true, ThrowAfterMutation = afterMutation };
        var journal = Journal();
        var result = await new RecoveryCoordinator<StorageState>(journal, adapter).RunAsync(_correlation,
            new(_before, _before with { LengthBytes = 50000000 }));
        Assert.Equal(afterMutation ? MutationOutcome.AppliedAndVerified : MutationOutcome.NotApplied, result);
        Assert.Contains(journal.Read(_correlation.OperationId), e => e.Kind == ExecutionRecordKind.ApplyInterrupted);
    }

    [Fact]
    public async Task UncertainObservationNeverAuthorizesApplyEvenAfterStateBecomesExact()
    {
        var machine = new FakeMachine<StorageState>(_before);
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        var journal = Journal();
        var coordinator = new RecoveryCoordinator<StorageState>(journal, new FakeAdapter<StorageState>(machine, _before) { Uncertain = true });
        Assert.Equal(MutationOutcome.AmbiguousRecoveryRequired, await coordinator.RunAsync(_correlation, mutation));
        Assert.Equal(0, machine.ApplyCount);
        machine.State = mutation.After;
        Assert.Equal(MutationOutcome.AppliedAndVerified, await new RecoveryCoordinator<StorageState>(Journal(),
            new FakeAdapter<StorageState>(machine, _before)).RunAsync(_correlation, mutation));
        Assert.Equal(0, machine.ApplyCount);
    }

    [Theory]
    [InlineData(ExecutionRecordKind.Intent, 0)]
    [InlineData(ExecutionRecordKind.VerifiedReceipt, 1)]
    public async Task FailedJournalWriteCannotCauseBlindRetry(ExecutionRecordKind failAt, int applied)
    {
        var durable = Journal();
        var faulting = new FaultingJournal(durable, failAt);
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        var machine = new FakeMachine<StorageState>(_before);
        var coordinator = new RecoveryCoordinator<StorageState>(faulting, new FakeAdapter<StorageState>(machine, _before));
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(_correlation, mutation));
        Assert.Equal(applied, machine.ApplyCount);
        if (failAt == ExecutionRecordKind.Intent)
            Assert.Empty(durable.Read(_correlation.OperationId));
        else
        {
            Assert.Equal(ExecutionRecordKind.Intent, Assert.Single(durable.Read(_correlation.OperationId)).Kind);
            Assert.Equal(MutationOutcome.AppliedAndVerified, await new RecoveryCoordinator<StorageState>(Journal(),
                new FakeAdapter<StorageState>(machine, _before)).RecoverAsync(_correlation.OperationId));
            Assert.Equal(1, machine.ApplyCount);
        }
    }

    [Fact]
    public async Task ReceiptDoesNotOverrideSubsequentGeometryDrift()
    {
        var machine = new FakeMachine<StorageState>(_before);
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        var coordinator = new RecoveryCoordinator<StorageState>(Journal(), new FakeAdapter<StorageState>(machine, _before));
        Assert.Equal(MutationOutcome.AppliedAndVerified, await coordinator.RunAsync(_correlation, mutation));
        machine.State = _before with { LengthBytes = 70000000 };
        Assert.Equal(MutationOutcome.AmbiguousRecoveryRequired, await coordinator.RecoverAsync(_correlation.OperationId));
        Assert.Equal(1, machine.ApplyCount);
    }

    [Fact]
    public async Task TwoCoordinatorsCannotExecuteConcurrently()
    {
        var journal = Journal();
        var secondJournal = Journal();
        var machine = new FakeMachine<StorageState>(_before);
        var mutation = new AuthorizedMutation<StorageState>(_before, _before with { LengthBytes = 50000000 });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new RecoveryCoordinator<StorageState>(journal, new FakeAdapter<StorageState>(machine, _before)
        {
            BeforeApply = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
        });
        var second = new RecoveryCoordinator<StorageState>(secondJournal, new FakeAdapter<StorageState>(machine, _before));
        var running = Task.Run(() => first.RunAsync(_correlation, mutation));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Assert.ThrowsAsync<IOException>(() => second.RunAsync(_correlation, mutation));
        }
        finally { release.Set(); }
        Assert.Equal(MutationOutcome.AppliedAndVerified, await running);
        Assert.Equal(MutationOutcome.AppliedAndVerified, await second.RunAsync(_correlation, mutation));
        Assert.Equal(1, machine.ApplyCount);
    }

    [Fact]
    public async Task RejectionRequiresObservedUnchangedState()
    {
        var machine = new FakeMachine<StorageState>(_before);
        var coordinator = new RecoveryCoordinator<StorageState>(Journal(), new FakeAdapter<StorageState>(machine, _before) { Reject = true });
        Assert.Equal(MutationOutcome.FailedWithoutMutation, await coordinator.RunAsync(_correlation,
            new(_before, _before with { LengthBytes = 50000000 })));
        Assert.Equal(0, machine.ApplyCount);
        Assert.Equal(MutationOutcome.NotApplied, await coordinator.RecoverAsync(_correlation.OperationId));
    }

    [Fact]
    public async Task DriftAfterIntentIsRejectedAtApplyBoundary()
    {
        var machine = new FakeMachine<StorageState>(_before);
        var coordinator = new RecoveryCoordinator<StorageState>(Journal(), new FakeAdapter<StorageState>(machine, _before)
        {
            BeforeApply = () => machine.State = _before with { LengthBytes = 75000000 }
        });
        Assert.Equal(MutationOutcome.AmbiguousRecoveryRequired, await coordinator.RunAsync(_correlation,
            new(_before, _before with { LengthBytes = 50000000 })));
        Assert.Equal(0, machine.ApplyCount);
    }

    [Fact]
    public void JournalRejectsChangedIntentAndNonSequentialEvents()
    {
        var journal = Journal();
        var intent = new ExecutionRecord<AuthorizedMutation<StorageState>>(1, 0, _correlation,
            new(_before, _before with { LengthBytes = 50000000 }), ExecutionRecordKind.Intent, DateTimeOffset.UtcNow, null, null);
        using var guard = journal.Acquire();
        journal.Append(intent);
        Assert.Throws<InvalidDataException>(() => journal.Append(intent with { Sequence = 1 }));
        Assert.Throws<InvalidDataException>(() => journal.Append(intent with { Sequence = 2, Kind = ExecutionRecordKind.ObservedNotApplied }));
        Assert.Single(journal.Read(_correlation.OperationId));
    }

    [Theory]
    [InlineData(BootRecoverySupport.Partial)]
    [InlineData(BootRecoverySupport.Unsupported)]
    public async Task IncompleteBootSnapshotCannotAuthorizeRestoration(BootRecoverySupport support)
    {
        var state = new BootState("fake-v1", "all", "current");
        var adapter = new FakeBootAdapter(state);
        var snapshot = new BootSnapshot(state with { CanonicalContent = "original" }, support);
        var result = await new BootRestorationAdapter(adapter, snapshot).ApplyAsync(new(state, snapshot.State), default);
        Assert.Equal(ApplyDisposition.RejectedBeforeMutation, result);
        Assert.Equal(state, (await adapter.InspectAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootCrashAndRestorationUseDurableRecovery(bool duringRestore)
    {
        var before = new BootState("fake-v1", "all-boot-state", "original");
        var boot = new FakeBootAdapter(before);
        var snapshot = await boot.CaptureAsync(default);
        var changed = before with { CanonicalContent = "authorized" };
        var path = Path.Join(_directory, "boot.db");
        var mutation = new AuthorizedMutation<BootState>(before, changed);
        boot.CrashDuringMutation = !duringRestore;
        var coordinator = new RecoveryCoordinator<BootState>(new SqliteExecutionJournal<AuthorizedMutation<BootState>>(path), boot);
        if (!duringRestore)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(_correlation, mutation));
            Assert.Equal(MutationOutcome.AmbiguousRecoveryRequired, await coordinator.RunAsync(_correlation, mutation, true));
        }
        else
        {
            await coordinator.RunAsync(_correlation, mutation);
        }
        var actual = (await boot.InspectAsync(default)).State!;
        var restoration = new AuthorizedMutation<BootState>(actual, snapshot.State);
        var restoreId = _correlation with { OperationId = Guid.NewGuid() };
        boot.CrashDuringRestore = duringRestore;
        var restoreCoordinator = new RecoveryCoordinator<BootState>(new SqliteExecutionJournal<AuthorizedMutation<BootState>>(path),
            new BootRestorationAdapter(boot, snapshot));
        if (duringRestore)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => restoreCoordinator.RunAsync(restoreId, restoration));
            Assert.Equal(MutationOutcome.AmbiguousRecoveryRequired, await restoreCoordinator.RunAsync(restoreId, restoration, true));
        }
        else
            Assert.Equal(MutationOutcome.AppliedAndVerified, await restoreCoordinator.RunAsync(restoreId, restoration));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    public enum CrashPoint { None, BeforeMutation, DuringMutation, AfterMutation, AfterVerification }
    private sealed class FaultingJournal(IExecutionJournal<AuthorizedMutation<StorageState>> inner, ExecutionRecordKind failAt)
        : IExecutionJournal<AuthorizedMutation<StorageState>>
    {
        public IDisposable Acquire() => inner.Acquire();
        public IReadOnlyList<ExecutionRecord<AuthorizedMutation<StorageState>>> Read(Guid operationId) => inner.Read(operationId);
        public void Append(ExecutionRecord<AuthorizedMutation<StorageState>> record)
        {
            if (record.Kind == failAt) throw new IOException("Simulated durable write failure");
            inner.Append(record);
        }
    }
    private sealed class FakeMachine<T>(T state) where T : notnull
    {
        public T State { get; set; } = state;
        public int ApplyCount { get; set; }
    }
    private sealed class FakeAdapter<T>(FakeMachine<T> machine, T partial) : IRecoverableMutationAdapter<T> where T : notnull
    {
        public CrashPoint Crash { get; init; }
        public Action? BeforeApply { get; set; }
        public bool ThrowOrdinary { get; init; }
        public bool ThrowAfterMutation { get; init; }
        public bool Uncertain { get; init; }
        public bool Reject { get; init; }
        public Task<MutationObservation<T>> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(new MutationObservation<T>(machine.State, Uncertain ? "Incomplete inspection" : null));
        public Task<ApplyDisposition> ApplyAsync(AuthorizedMutation<T> mutation, CancellationToken cancellationToken)
        {
            BeforeApply?.Invoke();
            if (!EqualityComparer<T>.Default.Equals(machine.State, mutation.Before)) return Task.FromResult(ApplyDisposition.RejectedBeforeMutation);
            if (Reject) return Task.FromResult(ApplyDisposition.RejectedBeforeMutation);
            if (ThrowOrdinary && !ThrowAfterMutation) throw new IOException("Simulated adapter error");
            if (Crash == CrashPoint.BeforeMutation) throw new OperationCanceledException("Simulated process loss");
            machine.ApplyCount++;
            machine.State = Crash == CrashPoint.DuringMutation ? partial : mutation.After;
            if (ThrowOrdinary) throw new IOException("Simulated adapter error");
            if (Crash is CrashPoint.DuringMutation or CrashPoint.AfterMutation) throw new OperationCanceledException("Simulated process loss");
            return Task.FromResult(ApplyDisposition.Attempted);
        }
        public Task<MutationVerification<T>> VerifyAsync(T expected, CancellationToken cancellationToken)
        {
            var verification = new MutationVerification<T>(EqualityComparer<T>.Default.Equals(machine.State, expected)
                ? VerificationDisposition.Exact : VerificationDisposition.Different, new(machine.State, null));
            if (Crash == CrashPoint.AfterVerification) throw new OperationCanceledException("Simulated process loss");
            return Task.FromResult(verification);
        }
    }
    private sealed class FakeBootAdapter(BootState state) : IRecoverableBootAdapter
    {
        private BootState _state = state;
        public bool PartialRestore { get; set; }
        public bool CrashDuringMutation { get; set; }
        public bool CrashDuringRestore { get; set; }
        public Task<BootSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new BootSnapshot(_state, BootRecoverySupport.Exact));
        public Task<MutationObservation<BootState>> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(new MutationObservation<BootState>(_state, null));
        public Task<ApplyDisposition> ApplyAsync(AuthorizedMutation<BootState> mutation, CancellationToken cancellationToken)
        {
            if (_state != mutation.Before) return Task.FromResult(ApplyDisposition.RejectedBeforeMutation);
            _state = CrashDuringMutation ? mutation.After with { CanonicalContent = "partial-mutation" } : mutation.After;
            if (CrashDuringMutation) throw new OperationCanceledException("Simulated boot mutation crash");
            return Task.FromResult(ApplyDisposition.Attempted);
        }
        public Task<MutationVerification<BootState>> VerifyAsync(BootState expected, CancellationToken cancellationToken) =>
            Task.FromResult(new MutationVerification<BootState>(_state == expected ? VerificationDisposition.Exact : VerificationDisposition.Different, new(_state, null)));
        public async Task<BootRestoration> RestoreAsync(BootSnapshot snapshot, BootState expectedBefore, CancellationToken cancellationToken)
        {
            if (_state != expectedBefore)
                return new(BootRecoverySupport.Unsupported, await VerifyAsync(snapshot.State, cancellationToken));
            _state = PartialRestore || CrashDuringRestore ? snapshot.State with { CanonicalContent = "partially-restored" } : snapshot.State;
            if (CrashDuringRestore) throw new OperationCanceledException("Simulated boot restoration crash");
            return new(PartialRestore ? BootRecoverySupport.Partial : BootRecoverySupport.Exact, await VerifyAsync(snapshot.State, cancellationToken));
        }
    }
}
