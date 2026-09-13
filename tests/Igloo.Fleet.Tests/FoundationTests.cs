using System.Text.Json;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void RegistrationRoundTripsWithExplicitProtocol()
    {
        var value = new AgentRegistrationRequest(FleetProtocolVersion.Current,
            new(Guid.NewGuid(), Guid.NewGuid()), "0.1.0", "1.0.0", new(true));
        Assert.Equal(value, JsonSerializer.Deserialize<AgentRegistrationRequest>(JsonSerializer.Serialize(value)));
        Assert.True(new FleetProtocolVersion(1, 0).IsSupported);
        Assert.False(new FleetProtocolVersion(1, 1).IsSupported);
        Assert.False(new FleetProtocolVersion(2, 0).IsSupported);
        Assert.Equal("{\"Major\":1,\"Minor\":0}", JsonSerializer.Serialize(FleetProtocolVersion.Current));
    }

    [Fact]
    public void LifecycleRequiresEveryApprovalAndExecutionStage()
    {
        var run = new MigrationRun(Guid.NewGuid(), Guid.NewGuid());
        foreach (var state in new[] { MigrationState.Assessed, MigrationState.Eligible, MigrationState.Approved,
            MigrationState.Scheduled, MigrationState.Preparing, MigrationState.Prepared, MigrationState.Migrating,
            MigrationState.FirstBoot, MigrationState.Validating, MigrationState.Completed })
            run = run.TransitionTo(state);
        Assert.Equal(MigrationState.Completed, run.State);
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(MigrationState.Migrating));
    }

    [Fact]
    public void CannotSkipAssessmentOrLeaveTerminalState()
    {
        var run = new MigrationRun(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(MigrationState.Approved));
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo((MigrationState)999));
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(MigrationState.Cancelled).TransitionTo(MigrationState.Assessed));
    }

    [Fact]
    public void RecoveryAndReassessmentAreExplicit()
    {
        var run = new MigrationRun(Guid.NewGuid(), Guid.NewGuid()).TransitionTo(MigrationState.Assessed)
            .TransitionTo(MigrationState.Blocked).TransitionTo(MigrationState.Assessed)
            .TransitionTo(MigrationState.Eligible).TransitionTo(MigrationState.Approved)
            .TransitionTo(MigrationState.Scheduled).TransitionTo(MigrationState.Preparing)
            .TransitionTo(MigrationState.RecoveryRequired).TransitionTo(MigrationState.Recovering)
            .TransitionTo(MigrationState.Recovered);
        Assert.Equal(MigrationState.Recovered, run.State);
        Assert.Throws<InvalidOperationException>(() => run.TransitionTo(MigrationState.Migrating));
    }

    [Theory]
    [InlineData(CheckStatus.Passed, EligibilityStatus.ReadyForReview)]
    [InlineData(CheckStatus.Unknown, EligibilityStatus.NeedsReview)]
    [InlineData(CheckStatus.Warning, EligibilityStatus.NeedsReview)]
    [InlineData(CheckStatus.Blocked, EligibilityStatus.Blocked)]
    public void EligibilityDoesNotTreatUnknownAsReady(CheckStatus status, EligibilityStatus expected) =>
        Assert.Equal(expected, EligibilityDecision.Evaluate([new(CheckId.BitLocker, status)], AssessmentOutcome.Assessed));
}
