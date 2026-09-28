using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianIsolationTests
{
    private static DebianDeploymentPlanV1 BoundPlan()
    {
        var plan = Plan(TargetRootFixture.Create());
        return plan with { Source = plan.Source with { OfflinePackageSet = DebianContentFoundationTests.PackageSet(plan) } };
    }

    [Fact]
    public void LaunchIsDerivedFromExactPlanAndToolWithoutEnablingProduction()
    {
        var plan = BoundPlan();
        var instruction = DebianNativeInstructions.Command(plan, DebianStage.ConfigureLocale);
        var tool = Observations.Available(new DebianQualifiedToolV1(instruction.Command.Executable, new string('B', 64), plan.Source.OfflinePackageSet!.RuntimeProfileSha256!));
        var launch = DebianIsolatedLaunches.Declare(plan, instruction, tool).Value;
        Assert.True(DebianIsolatedLaunches.Verify(plan, launch, tool).Value);
        Assert.Equal(0xdbUL, launch.CapabilityMask);
        Assert.Equal(plan.Root.GenerationId, launch.Mounts.GenerationId);
        Assert.Equal(DebianInputKind.None, launch.Instruction.Input);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("profile")]
    [InlineData("environment")]
    [InlineData("root")]
    [InlineData("generation")]
    [InlineData("hash")]
    [InlineData("tool")]
    public void ChangedLaunchRejected(string change)
    {
        var plan = BoundPlan(); var instruction = DebianNativeInstructions.Command(plan, DebianStage.ConfigureLocale);
        var tool = Observations.Available(new DebianQualifiedToolV1(instruction.Command.Executable, new string('B', 64), plan.Source.OfflinePackageSet!.RuntimeProfileSha256!));
        var launch = DebianIsolatedLaunches.Declare(plan, instruction, tool).Value;
        var changed = change switch
        {
            "capabilities" => launch with { CapabilityMask = ulong.MaxValue },
            "profile" => launch with { Profile = DebianExecutionProfile.Package },
            "environment" => launch with { Environment = launch.Environment.Add("LD_PRELOAD", "/host.so") },
            "root" => launch with { Mounts = launch.Mounts with { Root = "/" } },
            "generation" => launch with { GenerationId = Guid.NewGuid() },
            "hash" => launch with { PlanSha256 = new string('C', 64) },
            "tool" => launch with { ToolSha256 = new string('D', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(ObservationAvailability.Ambiguous, DebianIsolatedLaunches.Verify(plan, changed, tool).Availability);
    }

    [Theory]
    [InlineData(DebianStage.Bootstrap)]
    [InlineData(DebianStage.ConfigureSignedPackages)]
    [InlineData(DebianStage.FinalizeLoaderFiles)]
    [InlineData(DebianStage.FinalizeFirmware)]
    public void UnqualifiedStageCannotObtainPackageCapabilities(DebianStage stage)
    {
        var plan = BoundPlan();
        var instruction = new DebianNativeInstructionV1(stage, new("/usr/sbin/debootstrap", []), DebianInputKind.None);
        var tool = Observations.Available(new DebianQualifiedToolV1(instruction.Command.Executable, new string('B', 64), plan.Source.OfflinePackageSet!.RuntimeProfileSha256!));
        Assert.Equal(ObservationAvailability.Unsupported, DebianIsolatedLaunches.Declare(plan, instruction, tool).Availability);
    }

    [Fact]
    public void BootstrapCannotBecomeACommandEvenWithAnOfflineRuntimeBinding()
    {
        var plan = BoundPlan();
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(plan, DebianStage.Bootstrap));
    }

    [Theory]
    [InlineData("/usr/sbin/debootstrap")]
    [InlineData("/usr/bin/mmdebstrap")]
    [InlineData("/usr/bin/cdebootstrap")]
    [InlineData("/usr/sbin/debuerreotype-init")]
    [InlineData("/usr/bin/tar")]
    public void ReplacementBootstrapCannotBorrowAnExistingExecutionProfile(string executable)
    {
        var plan = BoundPlan();
        var tool = Observations.Available(new DebianQualifiedToolV1(executable, new string('B', 64), plan.Source.OfflinePackageSet!.RuntimeProfileSha256!));
        var instruction = new DebianNativeInstructionV1(DebianStage.Bootstrap, new(executable, []), DebianInputKind.None);
        Assert.Equal(ObservationAvailability.Unsupported, DebianIsolatedLaunches.Declare(plan, instruction, tool).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianIsolatedLaunches.Declare(plan,
            instruction with { Stage = DebianStage.ConfigureLocale }, tool).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianIsolatedLaunches.Declare(plan,
            instruction with { Stage = DebianStage.InstallDesktop }, tool).Availability);
    }

    [Fact]
    public void UnplannedArgvAndCredentialSubstitutionRejected()
    {
        var plan = BoundPlan(); var instruction = DebianNativeInstructions.Password;
        var tool = Observations.Available(new DebianQualifiedToolV1(instruction.Command.Executable, new string('B', 64), plan.Source.OfflinePackageSet!.RuntimeProfileSha256!));
        Assert.Equal(ObservationAvailability.Available, DebianIsolatedLaunches.Declare(plan, instruction, tool).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianIsolatedLaunches.Declare(plan, instruction with { Input = DebianInputKind.None }, tool).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianIsolatedLaunches.Declare(plan, instruction with { Command = instruction.Command with { Arguments = ["--root", "/host"] } }, tool).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Ambiguous)]
    [InlineData(ObservationAvailability.Absent)]
    public void ToolObservationStateIsPreserved(ObservationAvailability state)
    {
        var plan = BoundPlan();
        Assert.Equal(state, DebianIsolatedLaunches.Declare(plan, DebianNativeInstructions.Password,
            Observations.Failure<DebianQualifiedToolV1>(state, "fixture")).Availability);
    }
}
