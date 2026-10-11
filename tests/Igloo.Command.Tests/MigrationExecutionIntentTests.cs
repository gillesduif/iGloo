using Igloo.Command.Contracts;
using Igloo.Core.Runtime.Execution;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class MigrationExecutionIntentTests
{
    [Fact]
    public void CompleteIntentHasDeterministicVersionedIdentityAndEveryBoundFactMatters()
    {
        var intent = Fixture();
        var expected = MigrationExecutionIntentFingerprint.Compute(intent);
        Assert.Equal(64, expected.Length);
        Assert.Equal(expected, MigrationExecutionIntentFingerprint.Compute(intent with { }));

        var changed = new MigrationExecutionIntentV1[]
        {
            intent with { PlanId = Guid.NewGuid() },
            intent with { TargetId = Guid.NewGuid() },
            intent with { AllocationId = Guid.NewGuid() },
            intent with { LayoutId = Guid.NewGuid() },
            intent with { ArtifactId = "sha256-" + new string('1', 64) },
            intent with { RecoveryId = Guid.NewGuid() },
            intent with { PlanRevision = new string('1', 64) },
            intent with { ProviderRevision = new string('1', 64) },
            intent with { TargetRevision = new string('1', 64) },
            intent with { AllocationRevision = new string('1', 64) },
            intent with { LayoutRevision = new string('1', 64) },
            intent with { RecoveryRevision = new string('1', 64) },
        };
        foreach (var other in changed)
            Assert.NotEqual(expected, MigrationExecutionIntentFingerprint.Compute(other));
    }

    [Fact]
    public void IncompleteIntentCannotAcquireAnIdentity()
    {
        var intent = Fixture();
        Assert.Throws<ArgumentException>(() => MigrationExecutionIntentFingerprint.Compute(
            intent with { LayoutId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => MigrationExecutionIntentFingerprint.Compute(
            intent with { RecoveryId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => MigrationExecutionIntentFingerprint.Compute(
            intent with { ArtifactId = "unverified" }));
        Assert.Throws<ArgumentException>(() => MigrationExecutionIntentFingerprint.Compute(
            intent with { LayoutRevision = "unknown" }));
        Assert.Throws<ArgumentException>(() => MigrationExecutionIntentFingerprint.Compute(
            intent with { Risk = RiskClass.Low }));
    }

    private static MigrationExecutionIntentV1 Fixture() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "sha256-" + new string('A', 64), Guid.NewGuid(),
        new string('A', 64), new string('B', 64), new string('C', 64),
        new string('D', 64), new string('E', 64), new string('F', 64),
        RiskClass.Destructive);
}
