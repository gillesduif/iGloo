using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianHelperMountV1(string RelativePath, string FileSystem, string Mode, string Parent);
public enum DebianIsolationRequirement
{
    PrivateMountNamespace, PrivatePidNamespace, PreservedBlockWritesDenied, RawDiskWritesDenied,
    FirmwareWritesDenied, MountEscalationDenied, HostServicesInaccessible, NetworkDenied,
}
// Effective isolation must be independently acquired from the runtime backend, not requested
// flags or an exit code. This has no production acquisition implementation yet.
public sealed record DebianIsolationReadbackV1(Guid GenerationId, string PlanSha256,
    ImmutableArray<DebianIsolationRequirement> Enforced, string PolicyReadbackSha256);
public sealed record DebianRuntimeContextV1(Guid GenerationId,
    Observation<InstallerRuntimeInventoryV1> Inventory, Observation<TargetMountReadbackV1> Mounts,
    Observation<DebianIsolationReadbackV1> Isolation);

public static class DebianTargetBoundary
{
    // No recursive host /dev, /sys or /run bind. The source alias is explicit and read-only.
    // Device-node allowlisting and capability/firmware isolation require a qualified backend.
    public static ImmutableArray<DebianHelperMountV1> Helpers { get; } =
    [
        new("/dev", "tmpfs", "rw", ""), new("/dev/pts", "devpts", "rw", "/dev"),
        new("/proc", "proc", "rw", ""), new("/sys", "sysfs", "ro", ""),
        new("/run", "tmpfs", "rw", ""), new("/run/igloo-source", "vfat", "ro", "/run"),
    ];
    public static ImmutableArray<string> ReverseHelperUnmountOrder { get; } = Helpers.Reverse().Select(h => h.RelativePath).ToImmutableArray();

    public static Observation<ResolvedInstallationV1> Verify(DebianDeploymentPlanV1 plan, DebianStage stage,
        DebianRuntimeContextV1 context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var resolved = DebianDeploymentPlanning.Validate(plan, context.GenerationId, context.Inventory);
        if (resolved.Availability != ObservationAvailability.Available) return resolved;
        if (stage >= DebianStage.MountRoot && stage <= DebianStage.UnmountRoot)
        {
            if (context.Isolation.Availability != ObservationAvailability.Available)
                return Fail(context.Isolation.Availability, "DebianEffectiveIsolationUnavailable");
            var isolation = context.Isolation.Value;
            // The future firmware finalizer requires a separate narrow journal boundary. This
            // package namespace always denies firmware writes, even at that stage.
            if (isolation.GenerationId != context.GenerationId || isolation.PlanSha256 != DebianDeploymentPlanning.Fingerprint(plan) ||
                isolation.Enforced.IsDefault || !isolation.Enforced.Order().SequenceEqual(Enum.GetValues<DebianIsolationRequirement>().Order()) ||
                !DebianDeploymentPlanning.Hash(isolation.PolicyReadbackSha256))
                return Fail(ObservationAvailability.Ambiguous, "DebianIsolationChanged");
        }
        if (!DebianDeploymentStages.RequiresCompleteMounts(stage))
        {
            if (stage < DebianStage.MountRoot) return resolved;
            var partial = VerifyPartialMounts(plan, stage, context, resolved.Value);
            return partial.Availability == ObservationAvailability.Available ? resolved : Fail(partial.Availability, partial.Code!);
        }
        if (context.Mounts.Availability != ObservationAvailability.Available) return Fail(context.Mounts.Availability, "DebianMountReadUnavailable");
        var mountPlan = TargetRootMounts.Declare(plan.Root.GenerationId);
        var raw = context.Mounts.Value;
        if (raw.Mounts.IsDefaultOrEmpty || raw.Paths.IsDefault) return Fail(ObservationAvailability.Ambiguous, "DebianMountReadIncomplete");
        if (DebianDeploymentStages.RequiresHelpers(stage))
        {
            var helperMounts = new List<LinuxMountV1>();
            foreach (var helper in Helpers)
            {
                var path = mountPlan.Root + helper.RelativePath;
                var matches = raw.Mounts.Where(m => m.MountPoint == path).ToArray();
                var paths = raw.Paths.Where(p => p.RequestedPath == path).ToArray();
                if (matches.Length != 1 || paths.Length != 1 || paths[0].ResolvedPath != path || !paths[0].IsDirectory || paths[0].ContainsSymlink)
                    return Fail(ObservationAvailability.Ambiguous, "DebianHelperMissingOrSubstituted");
                var mount = matches[0];
                var parents = raw.Mounts.Where(m => m.MountPoint == mountPlan.Root + helper.Parent).ToArray();
                if (parents.Length != 1 || mount.ParentId != parents[0].MountId || mount.FileSystemRoot != "/" ||
                    mount.FileSystem != helper.FileSystem || mount.Options.IsDefault || !mount.Options.Contains(helper.Mode) ||
                    mount.Options.Count(o => o is "rw" or "ro") != 1 || mount.Propagation.IsDefault || !mount.Propagation.IsEmpty ||
                    mount.SuperOptions.IsDefault || (helper.Mode == "rw" && !mount.SuperOptions.Contains("rw")))
                    return Fail(ObservationAvailability.Ambiguous, "DebianHelperMountChanged");
                if (helper.RelativePath == "/run/igloo-source")
                {
                    var payloads = raw.Mounts.Where(m => m.MountPoint == mountPlan.Payload).ToArray();
                    if (payloads.Length != 1 || mount.Major != payloads[0].Major || mount.Minor != payloads[0].Minor)
                        return Fail(ObservationAvailability.Ambiguous, "DebianHelperSourceChanged");
                }
                else if (mount.Major != 0) return Fail(ObservationAvailability.Ambiguous, "DebianHelperUnexpectedBlockDevice");
                helperMounts.Add(mount);
            }
            // Remove ONLY fully inspected declared helpers. The shared verifier sees all other
            // mounts, so an extra ESP, efivarfs, preserved device alias or submount is rejected.
            var pathsToRemove = Helpers.Select(h => mountPlan.Root + h.RelativePath).ToHashSet(StringComparer.Ordinal);
            raw = raw with { Mounts = raw.Mounts.Except(helperMounts).ToImmutableArray(),
                Paths = raw.Paths.Where(p => !pathsToRemove.Contains(p.RequestedPath)).ToImmutableArray() };
        }
        var verified = TargetRootMounts.Verify(plan.Ownership, plan.Root, mountPlan, context.Inventory, Observations.Available(raw));
        return verified.Availability == ObservationAvailability.Available ? resolved : Fail(verified.Availability, verified.Code!);
    }

    private static Observation<bool> VerifyPartialMounts(DebianDeploymentPlanV1 plan, DebianStage stage,
        DebianRuntimeContextV1 context, ResolvedInstallationV1 resolved)
    {
        if (context.Mounts.Availability != ObservationAvailability.Available)
            return Observations.Failure<bool>(context.Mounts.Availability, "DebianPartialMountReadUnavailable");
        var raw = context.Mounts.Value;
        var mounts = TargetRootMounts.Declare(plan.Root.GenerationId);
        var rootExpected = stage is not (DebianStage.MountRoot or DebianStage.PersistCompletionEvidence);
        var espExpected = stage is DebianStage.MountPayload or DebianStage.UnmountLinuxEsp;
        var payloadExpected = stage is DebianStage.UnmountLinuxEsp or DebianStage.UnmountPayload;
        var expected = new[] { (mounts.Root, resolved.RootDevice, "ext4", "rw", rootExpected),
            (mounts.Esp, resolved.LinuxEspDevice, "vfat", "rw", espExpected),
            (mounts.Payload, resolved.PayloadDevice, "vfat", "ro", payloadExpected) };
        if (raw.Mounts.IsDefaultOrEmpty || raw.DeviceNumbers.IsDefault || raw.Paths.IsDefault ||
            raw.DeviceNumbers.Select(d => d.DevicePath).Distinct(StringComparer.Ordinal).Count() != raw.DeviceNumbers.Length ||
            raw.DeviceNumbers.Select(d => (d.Major, d.Minor)).Distinct().Count() != raw.DeviceNumbers.Length ||
            raw.Mounts.Select(m => m.MountId).Distinct().Count() != raw.Mounts.Length)
            return Bad("DebianPartialMountReadIncomplete");
        foreach (var (path, device, fs, mode, present) in expected)
        {
            var numbers = raw.DeviceNumbers.Where(d => d.DevicePath == device).ToArray();
            var found = raw.Mounts.Where(m => m.MountPoint == path).ToArray();
            if (numbers.Length != 1 || found.Length != (present ? 1 : 0)) return Bad("DebianMountSequenceChanged");
            var aliases = raw.Mounts.Where(m => m.Major == numbers[0].Major && m.Minor == numbers[0].Minor).ToArray();
            if (aliases.Length != found.Length) return Bad("DebianOwnedDeviceAliased");
            if (!present) continue;
            var p = raw.Paths.Where(p => p.RequestedPath == path).ToArray();
            if (p.Length != 1 || p[0].ResolvedPath != path || p[0].ContainsSymlink || !p[0].IsDirectory ||
                found[0].Major != numbers[0].Major || found[0].Minor != numbers[0].Minor ||
                found[0].FileSystem != fs || found[0].FileSystemRoot != "/" || found[0].Options.IsDefault ||
                !found[0].Options.Contains(mode) || found[0].Options.Count(o => o is "rw" or "ro") != 1 ||
                found[0].SuperOptions.IsDefault || (mode == "rw" && !found[0].SuperOptions.Contains("rw"))) return Bad("DebianPartialMountSubstituted");
        }
        var selectedPaths = expected.Where(e => e.Item5).Select(e => e.Item1).ToHashSet(StringComparer.Ordinal);
        if (raw.Mounts.Any(m => (Within(m.MountPoint, mounts.Root) || Within(m.MountPoint, mounts.Payload)) && !selectedPaths.Contains(m.MountPoint)) ||
            raw.Mounts.Any(m => (Within(mounts.Root, m.MountPoint) || Within(mounts.Payload, m.MountPoint)) && (m.Propagation.IsDefault || !m.Propagation.IsEmpty)))
            return Bad("DebianUnexpectedMountOrPropagation");
        if (espExpected && raw.Mounts.Single(m => m.MountPoint == mounts.Esp).ParentId != raw.Mounts.Single(m => m.MountPoint == mounts.Root).MountId)
            return Bad("DebianEspMountParentChanged");
        foreach (var partition in plan.Ownership.Layout.StorageOwnership!.PreservedPartitions)
        {
            var device = context.Inventory.Value.Partitions.Single(p => p.PartitionGuid == partition.PartitionGuid).DevicePath;
            var numbers = raw.DeviceNumbers.Where(d => d.DevicePath == device).ToArray();
            if (numbers.Length != 1 || raw.Mounts.Any(m => m.Major == numbers[0].Major && m.Minor == numbers[0].Minor &&
                (partition.PartitionGuid == plan.Ownership.Esp.WindowsEsp.Volume.PartitionGuid || m.Options.IsDefault || !m.Options.Contains("ro"))))
                return Bad("DebianPreservedStorageExposed");
        }
        return Observations.Available(true);
    }

    private static bool Within(string path, string parent) => path == parent || path.StartsWith(parent == "/" ? "/" : parent + "/", StringComparison.Ordinal);
    private static Observation<bool> Bad(string code) => Observations.Failure<bool>(ObservationAvailability.Ambiguous, code);

    private static Observation<ResolvedInstallationV1> Fail(ObservationAvailability state, string code) => Observations.Failure<ResolvedInstallationV1>(state, code);
}
