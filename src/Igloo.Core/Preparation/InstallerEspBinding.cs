using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Preparation;

public sealed record InstallerVolumeBindingV1(CanonicalVolumeIdentityV1 Volume, string FileSystemUuid);
public sealed record InstallerEspBindingV1(Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    InstallerVolumeBindingV1 WindowsEsp, InstallerVolumeBindingV1 LinuxEsp,
    InstallerVolumeBindingV1 Payload);

// Runtime paths are ephemeral results of one COMPLETE read-only Linux inventory, never persisted
// ownership. Native collection is not implemented here. Any failed probe must fail the whole
// Observation, not supply an empty array or default GUID. Filesystem UUID != Windows VolumeGuid.
public sealed record InstallerRuntimeDiskV1(string DevicePath, Guid GptDiskGuid, ulong SizeBytes, uint LogicalSectorSize);
// A successfully identified SquashFS has no UUID field. Null here is supported structural
// absence for that format only, not the result of a failed filesystem observation.
public sealed record InstallerFileSystemV1(string Type, string? Uuid);
public sealed record InstallerRuntimePartitionV1(string DevicePath, string DiskDevicePath, Guid PartitionGuid,
    Guid PartitionType, ulong OffsetBytes, ulong SizeBytes, Observation<InstallerFileSystemV1> FileSystem);
public sealed record InstallerRuntimeInventoryV1(ImmutableArray<InstallerRuntimeDiskV1> Disks,
    ImmutableArray<InstallerRuntimePartitionV1> Partitions)
{
    // Read-only loop/optical media also participate in UUID uniqueness checks.
    public ImmutableArray<InstallerExternalFileSystemV1> ExternalFileSystems { get; init; } = [];
}
public sealed record InstallerExternalFileSystemV1(string DevicePath, Observation<InstallerFileSystemV1> FileSystem);
public sealed record ResolvedInstallerEspV1(string LinuxEspDevice, string WindowsEspDevice, string PayloadDevice);

public static class InstallerEspBinding
{
    // Caller must first reopen/revalidate the preparation generation. This constructs a declaration,
    // not ownership, installer support, RecoverySnapshot Exactness or permission to install.
    public static Observation<InstallerEspBindingV1> Declare(PreparedLayoutV1 layout,
        Observation<string> linuxEspFileSystemUuid, Observation<string> windowsEspFileSystemUuid)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(linuxEspFileSystemUuid);
        ArgumentNullException.ThrowIfNull(windowsEspFileSystemUuid);
        if (linuxEspFileSystemUuid.Availability != ObservationAvailability.Available)
            return Failed<InstallerEspBindingV1>(linuxEspFileSystemUuid.Availability, "LinuxEspFilesystemIdentityUnavailable");
        if (windowsEspFileSystemUuid.Availability != ObservationAvailability.Available)
            return Failed<InstallerEspBindingV1>(windowsEspFileSystemUuid.Availability, "WindowsEspFilesystemIdentityUnavailable");
        var ownershipValid = layout.StorageOwnership is not null
            ? PreparedStorageOwnership.VerifyStructure(layout).Availability == ObservationAvailability.Available
            : !layout.Partitions.IsDefault && PreparedLayoutRules.AssessOwnership(layout.Plan, layout.Plan.GenerationId, layout.Partitions,
                layout.Partitions.Select(p => p.Identity).ToImmutableArray(), layout.Plan.WindowsEsp).State == PreparationState.CreatedAndVerified;
        if (!ownershipValid ||
            PreparedLayoutRules.VerifyBootFiles(layout, layout.BootFiles).Availability != ObservationAvailability.Available)
            return Failed<InstallerEspBindingV1>(ObservationAvailability.Ambiguous, "PreparedLayoutBindingInvalid");
        var binding = new InstallerEspBindingV1(layout.Plan.GenerationId, layout.Plan.TargetDisk,
            new(layout.Plan.WindowsEsp, windowsEspFileSystemUuid.Value.ToUpperInvariant()),
            new(layout.Partitions.Single(p => p.Role == PreparationRole.LinuxEsp).Identity, linuxEspFileSystemUuid.Value.ToUpperInvariant()),
            new(layout.Partitions.Single(p => p.Role == PreparationRole.Payload).Identity, layout.PayloadFileSystemUuid.ToUpperInvariant()));
        return IsValid(binding) ? Observations.Available(binding) :
            Failed<InstallerEspBindingV1>(ObservationAvailability.Ambiguous, "InstallerEspBindingInvalid");
    }

    public static Observation<ResolvedInstallerEspV1> Resolve(InstallerEspBindingV1 binding,
        Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(inventory);
        if (!IsValid(binding)) return Failed<ResolvedInstallerEspV1>(ObservationAvailability.Ambiguous, "InstallerEspBindingInvalid");
        if (inventory.Availability != ObservationAvailability.Available)
            return Failed<ResolvedInstallerEspV1>(inventory.Availability, "InstallerInventoryUnavailable");
        var valid = ValidateInventory(inventory.Value);
        if (valid.Availability != ObservationAvailability.Available)
            return Failed<ResolvedInstallerEspV1>(valid.Availability, valid.Code!);
        var observed = inventory.Value;
        var windows = Match(binding.WindowsEsp);
        var linux = Match(binding.LinuxEsp);
        var payload = Match(binding.Payload);
        foreach (var result in new[] { windows, linux, payload })
            if (result.Availability != ObservationAvailability.Available)
                return Failed<ResolvedInstallerEspV1>(result.Availability, result.Code!);
        return Observations.Available(new ResolvedInstallerEspV1(linux.Value, windows.Value, payload.Value));

        Observation<string> Match(InstallerVolumeBindingV1 expected)
        {
            var partition = observed.Partitions.SingleOrDefault(p => p.PartitionGuid == expected.Volume.PartitionGuid);
            if (partition is null) return Failed<string>(ObservationAvailability.Absent, "NominatedInstallerPartitionMissing");
            var disk = observed.Disks.Single(d => d.DevicePath == partition.DiskDevicePath);
            if (partition.FileSystem.Availability != ObservationAvailability.Available)
                return Failed<string>(partition.FileSystem.Availability, "NominatedInstallerFilesystemUnavailable");
            if (disk.GptDiskGuid != expected.Volume.Disk.GptDiskGuid || disk.SizeBytes != expected.Volume.Disk.SizeBytes ||
                disk.LogicalSectorSize != expected.Volume.Disk.LogicalSectorSize || partition.PartitionType != expected.Volume.PartitionType ||
                partition.OffsetBytes != expected.Volume.OffsetBytes || partition.SizeBytes != expected.Volume.SizeBytes ||
                !string.Equals(partition.FileSystem.Value.Uuid, expected.FileSystemUuid, StringComparison.OrdinalIgnoreCase) ||
                partition.FileSystem.Value.Type != "FAT32" || expected.Volume.FileSystem != "FAT32")
                return Failed<string>(ObservationAvailability.Ambiguous, "NominatedInstallerPartitionChanged");
            return Observations.Available(partition.DevicePath);
        }
    }

    public static Observation<bool> ValidateInventory(InstallerRuntimeInventoryV1 observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Disks.IsDefault || observed.Partitions.IsDefault || observed.ExternalFileSystems.IsDefault ||
            observed.Disks.Any(d => !SafePath(d.DevicePath) || d.GptDiskGuid == Guid.Empty || d.SizeBytes == 0 || d.LogicalSectorSize == 0) ||
            observed.Partitions.Any(p => !SafePath(p.DevicePath) || !SafePath(p.DiskDevicePath) || p.PartitionGuid == Guid.Empty ||
                p.PartitionType == Guid.Empty || p.SizeBytes == 0 || p.FileSystem is null ||
                (p.FileSystem.Availability == ObservationAvailability.Available &&
                    (string.IsNullOrWhiteSpace(p.FileSystem.Value.Type) || string.IsNullOrWhiteSpace(p.FileSystem.Value.Uuid)))) ||
            observed.Disks.Select(d => d.GptDiskGuid).Distinct().Count() != observed.Disks.Length ||
            observed.Disks.Select(d => d.DevicePath).Distinct(StringComparer.Ordinal).Count() != observed.Disks.Length ||
            observed.Partitions.Select(p => p.PartitionGuid).Distinct().Count() != observed.Partitions.Length ||
            observed.Partitions.Select(p => p.DevicePath).Distinct(StringComparer.Ordinal).Count() != observed.Partitions.Length ||
            observed.Partitions.Any(p => observed.Disks.Count(d => d.DevicePath == p.DiskDevicePath) != 1 ||
                observed.Disks.Any(d => d.DevicePath == p.DevicePath)))
            return Failed<bool>(ObservationAvailability.Ambiguous, "InstallerInventoryIncompleteOrDuplicate");
        var paths = observed.Disks.Select(d => d.DevicePath).Concat(observed.Partitions.Select(p => p.DevicePath))
            .Concat(observed.ExternalFileSystems.Select(e => e.DevicePath)).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length ||
            observed.ExternalFileSystems.Any(e => !SafePath(e.DevicePath) || e.FileSystem is null ||
                (e.FileSystem.Availability == ObservationAvailability.Available &&
                    (string.IsNullOrWhiteSpace(e.FileSystem.Value.Type) ||
                        (string.IsNullOrWhiteSpace(e.FileSystem.Value.Uuid) && !(e.FileSystem.Value.Type == "SQUASHFS" && e.FileSystem.Value.Uuid is null))))))
            return Failed<bool>(ObservationAvailability.Ambiguous, "InstallerExternalFilesystemInvalid");
        var filesystems = observed.Partitions.Select(p => p.FileSystem).Concat(observed.ExternalFileSystems.Select(e => e.FileSystem)).ToArray();
        var failed = filesystems.FirstOrDefault(f => f.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent));
        if (failed is not null) return Failed<bool>(failed.Availability, "InstallerFilesystemProbeFailed");
        var uuids = filesystems.Where(f => f.Availability == ObservationAvailability.Available && f.Value.Uuid is not null).Select(f => f.Value.Uuid).ToArray();
        if (uuids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != uuids.Length)
            return Failed<bool>(ObservationAvailability.Ambiguous, "InstallerFilesystemDuplicate");
        foreach (var partition in observed.Partitions)
        {
            var disk = observed.Disks.Single(d => d.DevicePath == partition.DiskDevicePath);
            if (partition.OffsetBytes > disk.SizeBytes || partition.SizeBytes > disk.SizeBytes - partition.OffsetBytes ||
                partition.OffsetBytes % disk.LogicalSectorSize != 0 || partition.SizeBytes % disk.LogicalSectorSize != 0 ||
                observed.Partitions.Any(other => other.PartitionGuid != partition.PartitionGuid &&
                    other.DiskDevicePath == partition.DiskDevicePath && Overlap(partition.OffsetBytes, partition.SizeBytes, other.OffsetBytes, other.SizeBytes)))
                return Failed<bool>(ObservationAvailability.Ambiguous, "InstallerInventoryGeometryInvalid");
        }
        return Observations.Available(true);
    }

    // Proven syntax in Fedora 44-1.7: Anaconda 44.30 + pykickstart 3.69. This is ONLY
    // an ESP directive, not a complete root/storage recipe or enabled distro adapter.
    // No directive is emitted unless the nominated UUID is unique in the fresh inventory.
    public static Observation<string> FedoraExistingEspDirective(InstallerEspBindingV1 binding,
        Observation<InstallerRuntimeInventoryV1> inventory)
    {
        var resolved = Resolve(binding, inventory);
        return resolved.Availability == ObservationAvailability.Available
            ? Observations.Available($"part /boot/efi --onpart=UUID={binding.LinuxEsp.FileSystemUuid} --noformat\n")
            : Failed<string>(resolved.Availability, resolved.Code!);
    }

    public static Observation<string> PartmanExistingEspDirective(string distroId)
    {
        // Inspected artifacts: Debian partman-efi 110/partman-auto 177, and Mint
        // Ubiquity 24.04.3+mint19. No proven unattended exact-ESP recipe is exposed.
        ArgumentNullException.ThrowIfNull(distroId);
        return Failed<string>(ObservationAvailability.Unsupported, distroId switch
        {
            "debian" => "DebianNominatedEspSelectionUnproven",
            "linuxmint-cinnamon" => "MintNominatedEspSelectionUnproven",
            _ => "InstallerProfileNotInspected",
        });
    }

    private static bool IsValid(InstallerEspBindingV1 binding)
    {
        var volumes = new[] { binding.WindowsEsp, binding.LinuxEsp, binding.Payload };
        return binding.GenerationId != Guid.Empty &&
            volumes.All(v => CanonicalRecoveryIdentity.IsValid(v.Volume) && v.Volume.FileSystem == "FAT32" && FatUuid(v.FileSystemUuid)) &&
            binding.LinuxEsp.Volume.Disk == binding.TargetDisk && binding.Payload.Volume.Disk == binding.TargetDisk &&
            binding.WindowsEsp.Volume.PartitionType == PreparationSpacePlanning.EspType &&
            binding.LinuxEsp.Volume.PartitionType == PreparationSpacePlanning.EspType &&
            binding.Payload.Volume.PartitionType == PreparationSpacePlanning.BasicDataType &&
            volumes.Select(v => v.Volume.PartitionGuid).Distinct().Count() == 3 &&
            volumes.Select(v => v.Volume.VolumeGuid).Distinct().Count() == 3 &&
            volumes.Select(v => v.FileSystemUuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 &&
            !volumes.Any(v => volumes.Any(other => v.Volume.PartitionGuid != other.Volume.PartitionGuid &&
                v.Volume.Disk.GptDiskGuid == other.Volume.Disk.GptDiskGuid &&
                (v.Volume.Disk != other.Volume.Disk || Overlap(v.Volume.OffsetBytes, v.Volume.SizeBytes, other.Volume.OffsetBytes, other.Volume.SizeBytes))));
    }

    private static bool Overlap(ulong start, ulong size, ulong otherStart, ulong otherSize) =>
        start <= otherStart ? otherStart - start < size : start - otherStart < otherSize;
    private static bool FatUuid(string value) => value is { Length: 9 } && value[4] == '-' &&
        value.Where((_, index) => index != 4).All(Uri.IsHexDigit);
    private static bool SafePath(string value) => value is not null && value.StartsWith("/dev/", StringComparison.Ordinal) &&
        value.Length > 5 && value[5..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '/');
    private static Observation<T> Failed<T>(ObservationAvailability state, string code) => Observations.Failure<T>(state, code);
}
