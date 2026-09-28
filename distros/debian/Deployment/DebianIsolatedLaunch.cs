using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianExecutionProfile { Package, Configuration }
public sealed record DebianQualifiedToolV1(string Executable, string Sha256, string RuntimeProfileSha256);
public sealed record DebianIsolatedLaunchV1(Guid GenerationId, string PlanSha256, string RuntimeProfileSha256,
    DebianNativeInstructionV1 Instruction, string OperationSha256, DebianExecutionProfile Profile,
    string ToolSha256, TargetRootMountPlanV1 Mounts, ulong CapabilityMask, int TimeoutSeconds,
    ImmutableSortedDictionary<string, string> Environment);

// Typed bridge for the native package broker. A declaration is not an isolation
// observation, stage success, or production capability. The native mount/session
// adapter and the complete qualified runtime are still required.
public static class DebianIsolatedLaunches
{
    public static ImmutableSortedDictionary<string, string> Environment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin", ["LC_ALL"] = "C", ["HOME"] = "/root",
            ["DEBIAN_FRONTEND"] = "noninteractive", ["DEBCONF_NONINTERACTIVE_SEEN"] = "true",
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);

    public static Observation<DebianIsolatedLaunchV1> Declare(DebianDeploymentPlanV1 plan,
        DebianNativeInstructionV1 instruction, Observation<DebianQualifiedToolV1> tool, int timeoutSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(tool);
        var planHash = DebianDeploymentPlanning.Fingerprint(plan);
        if (tool.Availability != ObservationAvailability.Available)
            return Fail(tool.Availability, "DebianToolIdentityUnavailable");
        var profile = Profile(instruction.Stage);
        if (profile is null) return Fail(ObservationAvailability.Unsupported, "DebianExecutionProfileUnqualified");
        var runtimeHash = plan.Source.OfflinePackageSet?.RuntimeProfileSha256;
        if (runtimeHash is null || !DebianDeploymentPlanning.Hash(runtimeHash))
            return Fail(ObservationAvailability.Unsupported, "DebianQualifiedRuntimeRequired");
        if (tool.Value.Executable != instruction.Command.Executable || !DebianDeploymentPlanning.Hash(tool.Value.Sha256) ||
            tool.Value.RuntimeProfileSha256 != runtimeHash || timeoutSeconds is < 1 or > 7200)
            return Fail(ObservationAvailability.Ambiguous, "DebianLaunchBindingChanged");
        var expected = ImmutableArray.CreateBuilder<DebianNativeInstructionV1>();
        expected.Add(DebianNativeInstructions.Command(plan, instruction.Stage));
        if (instruction.Stage == DebianStage.ConfigurePackagePolicy) expected.Add(DebianNativeInstructions.BootPackagePolicy);
        if (instruction.Stage == DebianStage.ConfigureUser)
        {
            expected.Add(DebianNativeInstructions.Password);
            expected.Add(DebianNativeInstructions.LockRoot);
        }
        var operationHash = DebianNativeInstructions.Fingerprint(instruction);
        if (!expected.Any(candidate => DebianNativeInstructions.Fingerprint(candidate) == operationHash))
            return Fail(ObservationAvailability.Ambiguous, "DebianUnplannedNativeInstruction");
        return Observations.Available(new DebianIsolatedLaunchV1(plan.Root.GenerationId, planHash, runtimeHash,
            instruction, operationHash, profile.Value, tool.Value.Sha256, TargetRootMounts.Declare(plan.Root.GenerationId),
            profile == DebianExecutionProfile.Package ? 0x800000dbUL : 0xdbUL, timeoutSeconds, Environment));
    }

    public static Observation<bool> Verify(DebianDeploymentPlanV1 plan, DebianIsolatedLaunchV1 launch,
        Observation<DebianQualifiedToolV1> freshTool)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var expected = Declare(plan, launch.Instruction, freshTool, launch.TimeoutSeconds);
        if (expected.Availability != ObservationAvailability.Available)
            return Observations.Failure<bool>(expected.Availability, expected.Code!);
        // Includes argv/input, generation, runtime, tool, profile, environment,
        // capability mask and canonical-derived mount destinations in both directions.
        return JsonSerializer.Serialize(expected.Value) == JsonSerializer.Serialize(launch)
            ? Observations.Available(true)
            : Observations.Failure<bool>(ObservationAvailability.Ambiguous, "DebianIsolatedLaunchChanged");
    }

    private static DebianExecutionProfile? Profile(DebianStage stage) => stage switch
    {
        DebianStage.ConfigurePackagePolicy or DebianStage.InstallKernel or DebianStage.InstallFirmware or DebianStage.InstallDesktop => DebianExecutionProfile.Package,
        DebianStage.ConfigureLocale or DebianStage.ConfigureUser or DebianStage.ConfigureSudo or DebianStage.GenerateInitramfs => DebianExecutionProfile.Configuration,
        // In particular Bootstrap may not inherit this profile or gain SYS_ADMIN.
        _ => null,
    };

    private static Observation<DebianIsolatedLaunchV1> Fail(ObservationAvailability state, string code) =>
        Observations.Failure<DebianIsolatedLaunchV1>(state, code);
}
