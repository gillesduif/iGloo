using Igloo.Core.Abstractions;
using Igloo.Fleet.Agent.Execution;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class ExecutionBoundaryTests
{
    [Fact]
    public void ProtectedStateReopensAndNeverReusesAnExecution()
    {
        using var f = new BoundaryFixture();
        var reopened = new ProtectedExecutionState(f.Root, f.Acl);
        Assert.Equal(ProtectedStateStatus.Verified, reopened.Verify(f.Authority).Status);
        var opened = reopened.Open(f.Binding);
        Assert.Equal(ProtectedStateStatus.Verified, opened.Status);
        Assert.Equal(f.Authority.ManifestHash, opened.Authority.ManifestHash);
        Assert.Throws<IOException>(() => reopened.Create(f.Binding, new Dictionary<string, byte[]>()));
        var other = reopened.Create(f.Binding with { ExecutionId = Guid.NewGuid() }, new Dictionary<string, byte[]>());
        Assert.NotEqual(reopened.MutableDirectory(other), reopened.MutableDirectory(f.Authority));
        Assert.NotEqual(ProtectedStateStatus.Verified, reopened.Verify(f.Authority with { Binding = other.Binding }).Status);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("file:stream")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    public void TraversalAndWindowsPathAliasesAreRejected(string name)
    {
        using var f = new BoundaryFixture();
        Assert.Throws<InvalidDataException>(() => new ProtectedExecutionState(f.Root, f.Acl).Create(
            f.Binding with { ExecutionId = Guid.NewGuid() }, new Dictionary<string, byte[]> { [name] = [1] }));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("manifest")]
    public void ImmutableArtifactsFailClosed(string damage)
    {
        using var f = new BoundaryFixture();
        var directory = Path.GetDirectoryName(f.State.MutableDirectory(f.Authority))!;
        var artifact = Path.Combine(directory, "immutable", "snapshot.json");
        switch (damage)
        {
            case "changed": File.WriteAllText(artifact, "different"); break;
            case "missing": File.Delete(artifact); break;
            case "unexpected": File.WriteAllText(Path.Combine(directory, "immutable", "extra.json"), "extra"); break;
            case "manifest": File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}"); break;
        }
        Assert.NotEqual(ProtectedStateStatus.Verified, f.State.Verify(f.Authority).Status);
    }

    [Fact]
    public void ManifestIdentityAndAclAreVerifiedOnReopen()
    {
        using var f = new BoundaryFixture();
        Assert.Equal(ProtectedStateStatus.IdentityMismatch, f.State.Verify(f.Authority with
        { Binding = f.Binding with { PlanId = Guid.NewGuid() } }).Status);
        f.Acl.Allow = false;
        Assert.Equal(ProtectedStateStatus.AclRejected, f.State.Verify(f.Authority).Status);
        Assert.Equal(GateStatus.Blocked, PreCommitGate.Evaluate(f.Input() with { ProtectedState = f.State.Verify(f.Authority) }).Status);
    }

    [Fact]
    public async Task CompetingCreatorsPublishOnlyOneAuthority()
    {
        using var f = new BoundaryFixture();
        var binding = f.Binding with { ExecutionId = Guid.NewGuid() };
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try { new ProtectedExecutionState(f.Root, f.Acl).Create(binding, new Dictionary<string, byte[]>()); return true; }
            catch (IOException) { return false; }
        })));
        Assert.Single(results.Where(success => success));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("plan")]
    [InlineData("execution")]
    [InlineData("target")]
    [InlineData("evidence")]
    [InlineData("profile")]
    [InlineData("operation")]
    public void AuthorizationRejectsEveryCorrelationMismatch(string field)
    {
        using var f = new BoundaryFixture();
        var wrong = field switch
        {
            "endpoint" => f.Binding with { Endpoint = new(Guid.NewGuid(), Guid.NewGuid()) },
            "plan" => f.Binding with { PlanId = Guid.NewGuid() },
            "execution" => f.Binding with { ExecutionId = Guid.NewGuid() },
            "target" => f.Binding with { TargetFingerprint = "different" },
            "evidence" => f.Binding with { EvidenceHash = "different" },
            "profile" => f.Binding with { ProfileRevisionId = Guid.NewGuid() },
            _ => f.Binding with { Operations = [new(Guid.NewGuid(), ExecutionOperationType.BootConfiguration)] },
        };
        Assert.Equal(AuthorizationStatus.CorrelationMismatch, f.Store.Validate(f.Authorization.AuthorizationId, wrong).Status);
        Assert.Equal(AuthorizationStatus.CorrelationMismatch, f.Store.Consume(f.Authorization.AuthorizationId, wrong).Status);
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
    }

    [Fact]
    public void ExpiryAndLifetimeAreExplicit()
    {
        using var f = new BoundaryFixture();
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
        Assert.Equal(AuthorizationStatus.Malformed, f.Store.Issue(f.Authorization with { ExpiresAtUtc = f.Clock.GetUtcNow().AddHours(1) }));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AuthorizationStatus.Expired, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
        Assert.Equal(AuthorizationStatus.Expired, f.Store.Consume(f.Authorization.AuthorizationId, f.Binding).Status);
    }

    [Fact]
    public void ConsumptionSurvivesReconstructionAndCannotBeInitializedAway()
    {
        using var f = new BoundaryFixture();
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Consume(f.Authorization.AuthorizationId, f.Binding).Status);
        var reopened = new SqliteExecutionAuthorizationStore(f.Database, f.Clock);
        Assert.Equal(AuthorizationStatus.Consumed, reopened.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
        Assert.Equal(AuthorizationStatus.Consumed, reopened.Consume(f.Authorization.AuthorizationId, f.Binding).Status);
        Assert.Throws<IOException>(() => reopened.Initialize());
        Assert.Equal(AuthorizationStatus.AlreadyIssued, reopened.Issue(f.Authorization with { AuthorizationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task ConcurrentConsumersAcrossStoreInstancesHaveExactlyOneSuccess()
    {
        using var f = new BoundaryFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new SqliteExecutionAuthorizationStore(f.Database, f.Clock).Consume(f.Authorization.AuthorizationId, f.Binding).Status)));
        Assert.Single(results.Where(s => s == AuthorizationStatus.Valid));
        Assert.Equal(7, results.Count(s => s == AuthorizationStatus.Consumed));
    }

    [Fact]
    public void MissingAndCorruptDatabaseNeverCreatesAuthority()
    {
        using var f = new BoundaryFixture();
        var missing = Path.Combine(f.Directory, "missing.db");
        Assert.Equal(AuthorizationStatus.Unavailable, new SqliteExecutionAuthorizationStore(missing).Validate(Guid.NewGuid(), f.Binding).Status);
        Assert.False(File.Exists(missing));
        File.WriteAllText(f.Database, "corrupt authority");
        Assert.Equal(AuthorizationStatus.Unavailable, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
    }

    [Fact]
    public void IssuanceAndConsumptionRowsAreImmutable()
    {
        using var f = new BoundaryFixture();
        using var connection = new SqliteConnection($"Data Source={f.Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE execution_authority SET payload='{}'";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Consume(f.Authorization.AuthorizationId, f.Binding).Status);
        command.CommandText = "DELETE FROM execution_consumption";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void GateReadyUsingFakeRecoveryDoesNotConsumeOrWrite()
    {
        using var f = new BoundaryFixture();
        var before = EvidenceIntegrity.HashBytes(File.ReadAllBytes(f.Database));
        Assert.Equal(GateStatus.Ready, PreCommitGate.Evaluate(f.Input()).Status);
        Assert.Equal(GateStatus.Ready, PreCommitGate.Evaluate(f.Input()).Status);
        Assert.Equal(before, EvidenceIntegrity.HashBytes(File.ReadAllBytes(f.Database)));
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
    }

    [Theory]
    [InlineData("changed", GateBlockReason.TargetNotExact)]
    [InlineData("ambiguous", GateBlockReason.TargetNotExact)]
    [InlineData("unavailable", GateBlockReason.TargetNotExact)]
    [InlineData("oldplan", GateBlockReason.BindingRequired)]
    [InlineData("protected", GateBlockReason.ProtectedStateUnavailable)]
    [InlineData("recovery", GateBlockReason.RecoveryNotReady)]
    [InlineData("expired", GateBlockReason.AuthorizationInvalid)]
    [InlineData("consumed", GateBlockReason.AuthorizationInvalid)]
    [InlineData("plan", GateBlockReason.PlanInvalid)]
    public void GateBlocksEveryUnprovenPrerequisite(string failure, GateBlockReason expected)
    {
        using var f = new BoundaryFixture();
        var input = f.Input();
        input = failure switch
        {
            "changed" => input with { Storage = Observations.Available(f.Target.Snapshot() with { Partitions = [f.Target.Partition with { Size = Observations.Available(1UL) }] }) },
            "ambiguous" => input with { Storage = Observations.Failure<WindowsStorageSnapshot>(ObservationAvailability.Ambiguous, "duplicate") },
            "unavailable" => input with { Storage = Observations.Failure<WindowsStorageSnapshot>(ObservationAvailability.AccessDenied, "denied") },
            "oldplan" => input with { ApprovedTarget = null },
            "protected" => input with { ProtectedState = input.ProtectedState with { Status = ProtectedStateStatus.ArtifactMismatch } },
            "recovery" => input with { Recovery = RecoveryReadiness.Production },
            "expired" => input with { Now = f.Authorization.ExpiresAtUtc },
            "consumed" => input with { Authorization = new(AuthorizationStatus.Consumed, f.Authorization) },
            _ => input with { PlanValidity = input.PlanValidity with { IsValid = false } },
        };
        var result = PreCommitGate.Evaluate(input);
        Assert.Equal(GateStatus.Blocked, result.Status);
        Assert.Contains(expected, result.Reasons);
    }

    [Fact]
    public void GateReportsSimultaneousBlockersAndExactOperationCorrelation()
    {
        using var f = new BoundaryFixture();
        var input = f.Input() with { ApprovedTarget = null, Recovery = RecoveryReadiness.Production,
            Authorization = new(AuthorizationStatus.Expired), ProtectedState = f.State.Verify(f.Authority) with { Status = ProtectedStateStatus.AclRejected } };
        var result = PreCommitGate.Evaluate(input);
        Assert.Contains(GateBlockReason.BindingRequired, result.Reasons);
        Assert.Contains(GateBlockReason.RecoveryNotReady, result.Reasons);
        Assert.Contains(GateBlockReason.AuthorizationInvalid, result.Reasons);
        Assert.Contains(GateBlockReason.ProtectedStateUnavailable, result.Reasons);
        input = f.Input() with { Expected = f.Binding with { Operations = [new(Guid.NewGuid(), ExecutionOperationType.StorageResize)] } };
        Assert.Contains(GateBlockReason.AuthorizationCorrelationMismatch, PreCommitGate.Evaluate(input).Reasons);
        Assert.Contains(GateBlockReason.ProtectedStateCorrelationMismatch, PreCommitGate.Evaluate(input).Reasons);
    }

    [Fact]
    public void GateSourceHasNoObservationMutationOrConsumptionDependency()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Igloo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Igloo.Fleet.Agent", "Execution", "PreCommitGate.cs"));
        foreach (var forbidden in new[] { ".Consume(", "System.Management", "DllImport", "LibraryImport", "Process.", "File.", "Directory.",
            "IMutationAdapter", "DirectInstallService", "Sqlite", "IWindowsStorageReader" }) Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void PurePhaseOneValidityMatchesRefreshWithoutMutatingState()
    {
        using var f = new PlanningFixture();
        var identity = PlanningTests.Device(f);
        var (_, result) = PlanningTests.Work(f, identity);
        var approval = PlanningTests.Approve(f, f.Service.Submit(identity, result));
        var plan = f.Service.Prepare(approval.ApprovalId, "operator");
        var store = new SqlitePlanningStore(f.Path);
        var snapshot = store.Read(s => s);
        var before = EvidenceIntegrity.Hash(snapshot);
        Assert.True(PlanningValidity.Evaluate(snapshot, plan.PlanId, f.Clock.GetUtcNow()).IsValid);
        f.Clock.Advance(TimeSpan.FromHours(5));
        Assert.False(PlanningValidity.Evaluate(snapshot, plan.PlanId, f.Clock.GetUtcNow()).IsValid);
        Assert.Equal(before, EvidenceIntegrity.Hash(snapshot));
        Assert.Equal(before, store.Read(EvidenceIntegrity.Hash));
        Assert.Equal(Igloo.Fleet.Contracts.PreparedPlanState.Expired, Assert.Single(f.Service.Plans()).Status);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("evidence")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("workexpired")]
    public void PureValidityReusesPhaseOneInvalidationRules(string change)
    {
        using var f = new PlanningFixture();
        var identity = PlanningTests.Device(f);
        var (work, result) = PlanningTests.Work(f, identity);
        var approval = PlanningTests.Approve(f, f.Service.Submit(identity, result));
        var plan = f.Service.Prepare(approval.ApprovalId, "operator");
        var state = new SqlitePlanningStore(f.Path).Read(s => s);
        switch (change)
        {
            case "profile":
                var profile = work.Profile! with { RevisionId = Guid.NewGuid(), Revision = work.Profile!.Revision + 1 };
                state.Profiles.Add(profile.RevisionId, profile); break;
            case "evidence": state.Audit.Clear(); break;
            case "revoked": state.Approvals[approval.ApprovalId] = approval with { Status = Igloo.Fleet.Contracts.ApprovalState.Revoked }; break;
            case "disabled": state.Devices[identity.DeviceId] = state.Devices[identity.DeviceId] with { Status = Igloo.Fleet.Contracts.AgentTrustStatus.Disabled }; break;
            case "workexpired": state.Work[work.WorkItemId] = work with { ExpiresAtUtc = f.Clock.GetUtcNow() }; break;
        }
        var before = EvidenceIntegrity.Hash(state);
        Assert.False(PlanningValidity.Evaluate(state, plan.PlanId, f.Clock.GetUtcNow()).IsValid);
        Assert.Equal(before, EvidenceIntegrity.Hash(state));
    }

    [Fact]
    public void ExpiredValiditySnapshotAndActualConsumptionBlockGate()
    {
        using var f = new BoundaryFixture();
        var input = f.Input();
        Assert.Contains(GateBlockReason.PlanInvalid, PreCommitGate.Evaluate(input with { Now = input.Now.AddSeconds(1) }).Reasons);
        Assert.Equal(AuthorizationStatus.Valid, f.Store.Consume(f.Authorization.AuthorizationId, f.Binding).Status);
        var blocked = PreCommitGate.Evaluate(f.Input());
        Assert.Equal(GateStatus.Blocked, blocked.Status);
        Assert.Equal(AuthorizationStatus.Consumed, blocked.Authorization);
    }

    [Fact]
    public void FutureIssueMissingTokenAndUnsupportedSchemaFailClosed()
    {
        using var f = new BoundaryFixture();
        Assert.Equal(AuthorizationStatus.Missing, f.Store.Validate(Guid.NewGuid(), f.Binding).Status);
        Assert.Equal(AuthorizationStatus.NotYetValid, f.Store.Issue(f.Authorization with
        { IssuedAtUtc = f.Clock.GetUtcNow().AddMinutes(1), ExpiresAtUtc = f.Clock.GetUtcNow().AddMinutes(2) }));
        Assert.Equal(AuthorizationStatus.Malformed, f.Store.Issue(f.Authorization with { SchemaVersion = 99 }));
        using var connection = new SqliteConnection($"Data Source={f.Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=99";
        command.ExecuteNonQuery();
        Assert.Equal(AuthorizationStatus.Malformed, f.Store.Validate(f.Authorization.AuthorizationId, f.Binding).Status);
    }

    private sealed class FakeAcl : IProtectedDirectoryAcl
    {
        public bool Allow { get; set; } = true;
        public void CreateProtected(string path) => System.IO.Directory.CreateDirectory(path);
        public bool Verify(string path) => Allow && System.IO.Directory.Exists(path);
        public bool VerifyFile(string path) => Allow && File.Exists(path);
    }

    private sealed class BoundaryFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "igloo-b2-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Directory, "protected");
        public FakeAcl Acl { get; } = new();
        public TestClock Clock { get; } = new();
        public TargetRevalidationTests.Fixture Target { get; } = new();
        public ExecutionBinding Binding { get; }
        public ExecutionAuthorization Authorization { get; }
        public ProtectedExecutionState State { get; }
        public ProtectedStateAuthority Authority { get; }
        public string Database { get; }
        public SqliteExecutionAuthorizationStore Store { get; }
        public BoundaryFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Binding = new(Guid.NewGuid(), Target.Plan.PlanId, Target.Plan.Identity, Target.Plan.ProfileRevisionId,
                Target.Plan.EvidenceHash, 1, Target.Binding.TargetFingerprint, [new(Guid.NewGuid(), ExecutionOperationType.StorageResize)]);
            Authorization = new(1, Guid.NewGuid(), Binding, Clock.GetUtcNow(), Clock.GetUtcNow().AddMinutes(5));
            State = new(Root, Acl);
            Authority = State.Create(Binding, new Dictionary<string, byte[]> { ["snapshot.json"] = [1, 2, 3] });
            Database = Path.Combine(State.MutableDirectory(Authority), "authorization.db");
            Store = new(Database, Clock);
            Store.Initialize();
            Assert.Equal(AuthorizationStatus.Valid, Store.Issue(Authorization));
        }
        public PreCommitInputs Input() => new(Binding, new(Target.Plan, true, null, Clock.GetUtcNow()), Target.Binding,
            Observations.Available(Target.Snapshot()), Observations.Available(Target.BitLocker), State.Verify(Authority),
            new(RecoveryReadinessStatus.Ready, []), Store.Validate(Authorization.AuthorizationId, Binding), Clock.GetUtcNow());
        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
