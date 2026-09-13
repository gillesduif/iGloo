using System.Collections.Immutable;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class PlanningTests
{
    [Theory]
    [InlineData(100, 0, 1, null)]
    [InlineData(100, 10, 1, 10L)]
    [InlineData(100, 0, 30, 30L)]
    [InlineData(10, 0, 1, 10L)]
    public void StorageQueryAmbiguityDoesNotBecomeMeasuredInsufficientSpace(long totalGb, long shrinkGb, long freeGb, long? expectedGb)
    {
        var disks = new Igloo.Core.Abstractions.DiskInfo[]
        {
            new("private-id", "private-model", totalGb << 30, freeGb << 30, "GPT",
                [new(1, "NTFS", totalGb << 30, null, false, true, shrinkGb << 30)]),
        };
        Assert.Equal(expectedGb.HasValue ? expectedGb.Value << 30 : null, ReadOnlyPlanner.AvailableCapacity(disks, 20L << 30));
    }

    internal static DeviceIdentity Device(PlanningFixture fixture)
    {
        var token = fixture.Service.CreateToken(10, "operator");
        var identity = fixture.Service.Enroll(token.Token, _ => new("certificate", Guid.NewGuid().ToString(),
            fixture.Clock.GetUtcNow().AddDays(1))).Identity;
        fixture.Service.Heartbeat(identity, new(PlanningProtocol.Version, ReadOnlyAssessment.AgentVersion,
            ReadOnlyAssessment.IglooVersion, [.. Enum.GetValues<PlanningCapability>()]));
        return identity;
    }

    internal static (ReadOnlyWorkItem Work, ReadOnlyWorkResult Result) Work(PlanningFixture fixture, DeviceIdentity identity,
        MigrationProfileRevision? profile = null)
    {
        profile ??= fixture.Service.SaveProfile(null, new("Debian planning", "debian", 20L << 30, false), "operator");
        var work = fixture.Service.RequestWork(new(identity.DeviceId, ReadOnlyWorkType.RunMigrationDryRun, profile.RevisionId, 60), "operator");
        work = fixture.Service.Claim(identity)!;
        var report = AgentTests.Report with { Findings = [] };
        var assessment = ReadOnlyAssessment.Map(report, identity, fixture.Clock.GetUtcNow()) with { AssessmentId = work.WorkItemId };
        return (work, new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value, work.CorrelationId,
            profile.RevisionId, assessment, new(true, true, 100L << 30, 20L << 30, new string('A', 64), [])));
    }

    internal static PlanApproval Approve(PlanningFixture fixture, DryRunEvidence evidence, string? reason = null) =>
        fixture.Service.Approve(new(evidence.DryRunId, evidence.EvidenceHash, evidence.Result.ProfileRevisionId!.Value,
            evidence.Decision.DecisionId, reason), "operator");

    [Fact]
    public void HistoryHashesAndPreparedPlanSurviveReopeningDatabase()
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var (_, result) = Work(fixture, identity);
        var evidence = fixture.Service.Submit(identity, result);
        var retry = fixture.Service.Submit(identity, result);
        Assert.Equal(evidence.EvidenceHash, retry.EvidenceHash);
        Assert.Single(fixture.Service.Evidence());
        Assert.Equal(PlanningEligibility.Eligible, evidence.Decision.Status);
        var approval = Approve(fixture, evidence);
        var plan = fixture.Service.Prepare(approval.ApprovalId, "operator");
        Assert.Equal(PreparedPlanState.Prepared, plan.Status);
        Assert.Equal(evidence.EvidenceHash, plan.EvidenceHash);
        var restarted = new PlanningService(new SqlitePlanningStore(fixture.Path), fixture.Clock);
        Assert.Equal(plan, Assert.Single(restarted.Plans()));
        Assert.Equal(evidence.EvidenceHash, Assert.Single(restarted.Evidence()).EvidenceHash);
        Assert.Single(restarted.Profiles());
        Assert.Single(restarted.Approvals());
        Assert.Contains(restarted.AuditEvents(), a => a.Action == "PreparedPlanCreated");
        Assert.NotEqual(EvidenceIntegrity.Hash(result), EvidenceIntegrity.Hash(result with { CorrelationId = Guid.NewGuid() }));
        Assert.Equal(EvidenceIntegrity.Hash(result), EvidenceIntegrity.Hash(result));
        Assert.Equal(EvidenceIntegrity.Hash(new { B = 2, A = 1 }), EvidenceIntegrity.Hash(new { A = 1, B = 2 }));
    }

    [Theory]
    [InlineData(PlanningEligibility.NeedsReview)]
    [InlineData(PlanningEligibility.Blocked)]
    [InlineData(PlanningEligibility.Unknown)]
    public void NonEligibleApprovalPolicyIsExplicit(PlanningEligibility status)
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var (_, result) = Work(fixture, identity);
        var facts = result.Planning!;
        result = status switch
        {
            PlanningEligibility.NeedsReview => result with { Planning = facts with { Reasons = [PlanningReason.CompatibilityReview] } },
            PlanningEligibility.Blocked => result with { Planning = facts with { AvailableBytes = 0 } },
            _ => result with { Planning = facts with { Reasons = [PlanningReason.LocalReadFailure] } },
        };
        var evidence = fixture.Service.Submit(identity, result);
        Assert.Equal(status, evidence.Decision.Status);
        Assert.Throws<PlanningException>(() => Approve(fixture, evidence));
        if (status == PlanningEligibility.NeedsReview)
        {
            var approved = Approve(fixture, evidence, "Reviewed uncertainty; planning only.");
            Assert.Equal("operator", approved.ApprovedBy);
            Assert.NotNull(approved.ReviewReason);
        }
        else Assert.Throws<PlanningException>(() => Approve(fixture, evidence, "Cannot bypass blockers."));
    }

    [Fact]
    public void RevisionAndNewEvidenceInvalidateExactApprovals()
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var (work, result) = Work(fixture, identity);
        var evidence = fixture.Service.Submit(identity, result);
        var approval = Approve(fixture, evidence);
        var plan = fixture.Service.Prepare(approval.ApprovalId, "operator");
        var revision = fixture.Service.SaveProfile(work.Profile!.ProfileId, work.Profile.Spec with { Name = "Revision two" }, "operator");
        Assert.Equal(2, revision.Revision);
        Assert.Equal("Debian planning", fixture.Service.Profiles().Single(p => p.Revision == 1).Spec.Name);
        Assert.Throws<PlanningException>(() => fixture.Service.Prepare(approval.ApprovalId, "operator"));
        Assert.Equal(PreparedPlanState.Superseded, fixture.Service.Plans().Single(p => p.PlanId == plan.PlanId).Status);
        var (_, newer) = Work(fixture, identity, revision);
        var newEvidence = fixture.Service.Submit(identity, newer);
        var newApproval = Approve(fixture, newEvidence);
        fixture.Service.Prepare(newApproval.ApprovalId, "operator");
        fixture.Service.RevokeApproval(newApproval.ApprovalId, "operator");
        Assert.Equal(PreparedPlanState.Invalidated, fixture.Service.Plans().Single(p => p.ApprovalId == newApproval.ApprovalId).Status);
        Assert.Equal(2, fixture.Service.Evidence().Count);
    }

    [Fact]
    public void NewAssessmentExpiryTamperingAndUnknownWorkAreRejected()
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var (_, first) = Work(fixture, identity);
        var evidence = fixture.Service.Submit(identity, first);
        var approval = Approve(fixture, evidence);
        fixture.Service.Prepare(approval.ApprovalId, "operator");
        var (_, second) = Work(fixture, identity);
        Assert.Throws<PlanningException>(() => fixture.Service.Submit(identity, second with { ProfileRevisionId = Guid.NewGuid() }));
        fixture.Service.Submit(identity, second);
        Assert.Throws<PlanningException>(() => fixture.Service.Prepare(approval.ApprovalId, "operator"));
        Assert.Throws<PlanningException>(() => fixture.Service.RequestWork(new(identity.DeviceId, (ReadOnlyWorkType)99, null, 5), "operator"));
        fixture.Clock.Advance(TimeSpan.FromHours(5));
        Assert.All(fixture.Service.Plans(), p => Assert.NotEqual(PreparedPlanState.Prepared, p.Status));
    }

    [Fact]
    public async Task WorkClaimsAreExclusiveAndResultsCannotCrossIdentities()
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var other = Device(fixture);
        var (_, result) = Work(fixture, identity);
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => fixture.Service.Claim(identity))));
        Assert.All(claims, Assert.Null);
        Assert.Null(fixture.Service.Claim(other));
        Assert.Throws<PlanningException>(() => fixture.Service.Submit(other, result));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = fixture.Service.Claim(identity);
        Assert.NotNull(reclaimed);
        Assert.NotEqual(result.LeaseId, reclaimed.LeaseId);
        Assert.Throws<PlanningException>(() => fixture.Service.Submit(identity, result));
    }

    [Fact]
    public void InvalidProfilesCapabilitiesAndUnknownEvidenceFailClosed()
    {
        using var fixture = new PlanningFixture();
        Assert.Throws<PlanningException>(() => fixture.Service.SaveProfile(null, new("Unknown", "ubuntu", 20L << 30, false), "operator"));
        Assert.Throws<PlanningException>(() => fixture.Service.SaveProfile(null, new("Small", "debian", 1, false), "operator"));
        var identity = Device(fixture);
        fixture.Service.Heartbeat(identity, new(PlanningProtocol.Version, "0.1.0", ReadOnlyAssessment.IglooVersion, []));
        var profile = fixture.Service.SaveProfile(null, new("Debian", "debian", 20L << 30, false), "operator");
        Assert.Throws<PlanningException>(() => fixture.Service.RequestWork(new(identity.DeviceId, ReadOnlyWorkType.RunMigrationDryRun, profile.RevisionId, 10), "operator"));
    }

    [Fact]
    public void SpoolSurvivesRestartAndRejectsConflictingReplacement()
    {
        using var fixture = new PlanningFixture();
        var identity = Device(fixture);
        var (_, result) = Work(fixture, identity);
        var directory = System.IO.Path.Join(System.IO.Path.GetDirectoryName(fixture.Path), "outbox");
        var spool = new ResultSpool(directory);
        spool.Save(result);
        var saved = Assert.Single(new ResultSpool(directory).Pending());
        Assert.Equal(EvidenceIntegrity.Hash(result), EvidenceIntegrity.Hash(saved));
        Assert.Throws<InvalidDataException>(() => spool.Save(result with { CorrelationId = Guid.NewGuid() }));
        spool.Acknowledge(result.WorkItemId);
        Assert.Empty(spool.Pending());
    }
}
