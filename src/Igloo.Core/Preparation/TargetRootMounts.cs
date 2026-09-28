using System.Collections.Immutable;
using System.Globalization;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

// Transient kernel device numbers join stat(block device) to mountinfo. Neither path nor number
// is persisted ownership. The acquisition layer must read these in the same mount namespace.
public sealed record LinuxDeviceNumberV1(string DevicePath, uint Major, uint Minor);
public sealed record LinuxMountV1(uint MountId, uint ParentId, uint Major, uint Minor,
    string FileSystemRoot, string MountPoint, string FileSystem, ImmutableArray<string> Options,
    ImmutableArray<string> Propagation, ImmutableArray<string> SuperOptions);
public sealed record TargetPathReadbackV1(string RequestedPath, string ResolvedPath,
    bool IsDirectory, bool ContainsSymlink);
public sealed record TargetMountReadbackV1(ImmutableArray<LinuxMountV1> Mounts,
    ImmutableArray<LinuxDeviceNumberV1> DeviceNumbers, ImmutableArray<TargetPathReadbackV1> Paths);
public sealed record TargetRootMountPlanV1(Guid GenerationId, string Root, string Esp, string Payload);
public sealed record VerifiedTargetMountsV1(Guid GenerationId, uint RootMountId, uint EspMountId, uint PayloadMountId);

public static class LinuxMountInfo
{
    // Documented /proc/self/mountinfo fields, including kernel path escaping. Mount source text
    // is deliberately not used to establish ownership; major:minor must join fresh block stat.
    public static Observation<ImmutableArray<LinuxMountV1>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var mounts = ImmutableArray.CreateBuilder<LinuxMountV1>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (separator < 6 || fields.Length != separator + 4 || fields.Any(string.IsNullOrEmpty))
                    throw new FormatException("Invalid mountinfo shape.");
                var device = fields[2].Split(':');
                if (device.Length != 2) throw new FormatException("Invalid device number.");
                mounts.Add(new(Number(fields[0]), Number(fields[1]), Number(device[0]), Number(device[1]),
                    Unescape(fields[3]), Unescape(fields[4]), fields[separator + 1],
                    fields[5].Split(',').ToImmutableArray(), fields.Skip(6).Take(separator - 6).ToImmutableArray(),
                    fields[separator + 3].Split(',').ToImmutableArray()));
            }
            if (mounts.Count == 0 || mounts.Any(m => m.MountId == 0 || !m.FileSystemRoot.StartsWith('/') ||
                    !m.MountPoint.StartsWith('/') || m.Options.Count(o => o is "rw" or "ro") != 1) ||
                mounts.Select(m => m.MountId).Distinct().Count() != mounts.Count)
                throw new FormatException("Incomplete mountinfo.");
            return Observations.Available(mounts.ToImmutable());
        }
        catch (FormatException) { return Observations.Failure<ImmutableArray<LinuxMountV1>>(ObservationAvailability.Ambiguous, "MalformedMountInfo"); }
        catch (OverflowException) { return Observations.Failure<ImmutableArray<LinuxMountV1>>(ObservationAvailability.Ambiguous, "MalformedMountNumber"); }
    }

    private static uint Number(string value) => uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static string Unescape(string value)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\') { result.Append(value[index]); continue; }
            if (index + 3 >= value.Length) throw new FormatException("Short mount escape.");
            result.Append(value.Substring(index + 1, 3) switch
            {
                "040" => ' ', "011" => '\t', "012" => '\n', "134" => '\\',
                _ => throw new FormatException("Unknown mount escape."),
            });
            index += 3;
        }
        return result.ToString();
    }
}

// This verifies the core root/ESP/source mounts BEFORE chroot helper mounts are introduced.
// It neither mounts anything nor proves package hooks cannot open raw devices or firmware.
public static class TargetRootMounts
{
    public static TargetRootMountPlanV1 Declare(Guid generation)
    {
        if (generation == Guid.Empty) throw new ArgumentException("Missing preparation generation.", nameof(generation));
        var root = $"/run/igloo/target/{generation:D}";
        return new(generation, root, root + "/boot/efi", $"/run/igloo/source/{generation:D}");
    }

    public static Observation<VerifiedTargetMountsV1> Verify(InstallationOwnershipV1 ownership,
        RootFileSystemReceiptV1 root, TargetRootMountPlanV1 plan,
        Observation<InstallerRuntimeInventoryV1> inventory, Observation<TargetMountReadbackV1> readback)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readback);
        if (plan.GenerationId == Guid.Empty || plan != Declare(plan.GenerationId)) return Fail("TargetMountPlanChanged");
        var resolved = InstallationOwnership.ResolveFormattedRoot(ownership, plan.GenerationId, root, inventory);
        if (resolved.Availability != ObservationAvailability.Available) return Fail(resolved.Code!, resolved.Availability);
        if (readback.Availability != ObservationAvailability.Available) return Fail("TargetMountReadUnavailable", readback.Availability);
        var observed = readback.Value;
        if (observed.Mounts.IsDefaultOrEmpty || observed.DeviceNumbers.IsDefault || observed.Paths.IsDefault)
            return Fail("TargetMountReadIncomplete");
        var paths = inventory.Value.Disks.Select(d => d.DevicePath).Concat(inventory.Value.Partitions.Select(p => p.DevicePath))
            .Concat(inventory.Value.ExternalFileSystems.Select(e => e.DevicePath)).Order(StringComparer.Ordinal);
        if (!observed.DeviceNumbers.Select(d => d.DevicePath).Order(StringComparer.Ordinal).SequenceEqual(paths, StringComparer.Ordinal) ||
            observed.DeviceNumbers.Select(d => (d.Major, d.Minor)).Distinct().Count() != observed.DeviceNumbers.Length ||
            observed.Mounts.Select(m => m.MountId).Distinct().Count() != observed.Mounts.Length ||
            observed.Mounts.Any(m => m.MountId == 0 || m.Options.IsDefault || m.Propagation.IsDefault || m.SuperOptions.IsDefault ||
                m.Options.Count(o => o is "rw" or "ro") != 1 || m.SuperOptions.Count(o => o is "rw" or "ro") != 1)) return Fail("MountDeviceCorrelationInvalid");
        string[] expectedPaths = [plan.Root, plan.Esp, plan.Payload];
        if (observed.Paths.Length != 3 || !observed.Paths.Select(p => p.RequestedPath).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            observed.Paths.Any(p => p.RequestedPath != p.ResolvedPath || !p.IsDirectory || p.ContainsSymlink))
            return Fail("MountPathSubstituted");
        var declarations = new[] { (plan.Root, resolved.Value.RootDevice, "ext4", "rw"),
            (plan.Esp, resolved.Value.LinuxEspDevice, "vfat", "rw"), (plan.Payload, resolved.Value.PayloadDevice, "vfat", "ro") };
        var selected = new List<LinuxMountV1>();
        foreach (var (mountPath, devicePath, fileSystem, mode) in declarations)
        {
            var device = observed.DeviceNumbers.Single(d => d.DevicePath == devicePath);
            var matches = observed.Mounts.Where(m => m.MountPoint == mountPath).ToArray();
            if (matches.Length != 1) return Fail("OwnedMountMissingOrStacked");
            var mount = matches[0];
            if (mount.Major != device.Major || mount.Minor != device.Minor || mount.FileSystemRoot != "/" ||
                mount.FileSystem != fileSystem || !mount.Options.Contains(mode) ||
                (mode == "rw" && !mount.SuperOptions.Contains("rw"))) return Fail("OwnedMountChanged");
            // Reject aliases/binds of an owned writable filesystem anywhere in this namespace.
            if (observed.Mounts.Count(m => m.Major == device.Major && m.Minor == device.Minor) != 1)
                return Fail("OwnedMountAliased");
            selected.Add(mount);
        }
        foreach (var preserved in ownership.Layout.StorageOwnership!.PreservedPartitions)
        {
            var partition = inventory.Value.Partitions.Single(p => p.PartitionGuid == preserved.PartitionGuid);
            var device = observed.DeviceNumbers.Single(d => d.DevicePath == partition.DevicePath);
            if (observed.Mounts.Any(m => m.Major == device.Major && m.Minor == device.Minor &&
                (preserved.PartitionGuid == ownership.Esp.WindowsEsp.Volume.PartitionGuid || !m.Options.Contains("ro"))))
                return Fail("PreservedWindowsStorageMounted");
        }
        if (selected[1].ParentId != selected[0].MountId || observed.Mounts.Any(m =>
            (Within(m.MountPoint, plan.Root) || Within(m.MountPoint, plan.Payload)) && !selected.Contains(m)))
            return Fail("UnexpectedTargetSubmount");
        if (observed.Mounts.Any(m => expectedPaths.Any(p => Within(p, m.MountPoint)) && !m.Propagation.IsEmpty))
            return Fail("TargetMountPropagationUnbounded");
        return Observations.Available(new VerifiedTargetMountsV1(plan.GenerationId, selected[0].MountId, selected[1].MountId, selected[2].MountId));
    }

    public static Observation<string> GenerateFstab(InstallationOwnershipV1 ownership, RootFileSystemReceiptV1 root,
        TargetRootMountPlanV1 plan, Observation<InstallerRuntimeInventoryV1> inventory, Observation<TargetMountReadbackV1> readback)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(root);
        var verified = Verify(ownership, root, plan, inventory, readback);
        return verified.Availability == ObservationAvailability.Available ? Observations.Available(
            $"UUID={root.FileSystemUuid:D} / ext4 defaults,errors=remount-ro 0 1\n" +
            $"UUID={ownership.Esp.LinuxEsp.FileSystemUuid.ToUpperInvariant()} /boot/efi vfat umask=0077 0 1\n") :
            Observations.Failure<string>(verified.Availability, verified.Code!);
    }

    private static bool Within(string path, string parent) => path == parent ||
        path.StartsWith(parent == "/" ? "/" : parent + "/", StringComparison.Ordinal);
    private static Observation<VerifiedTargetMountsV1> Fail(string code, ObservationAvailability state = ObservationAvailability.Ambiguous) => Observations.Failure<VerifiedTargetMountsV1>(state, code);
}
