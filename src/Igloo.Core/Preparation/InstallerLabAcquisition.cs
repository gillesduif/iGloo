using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

// Lab facts are deliberately NOT CanonicalDiskIdentityV1/CanonicalVolumeIdentityV1.
// There is no conversion to a Windows preparation or recovery receipt. The host
// evidence input must be independently reopened by the exclusive lab controller.
public sealed record InstallerLabBackingV1(string Serial, ulong Length, ulong HostDevice,
    ulong HostInode, string CreationSha256, string LaunchSha256);
public sealed record InstallerLabGuestDiskV1(string DevicePath, string Serial, uint PhysicalSectorSize);
public sealed record InstallerLabDiskV1(InstallerLabBackingV1 Backing, InstallerRuntimeDiskV1 Disk,
    uint PhysicalSectorSize);
public sealed record InstallerLabAcquisitionV1(int SchemaVersion, Guid LabRunId,
    string HostObservationSha256, ImmutableArray<InstallerLabDiskV1> Disks);
public sealed record InstallerLabPartitionReceiptV1(Guid LabRunId, Guid GenerationId,
    InstallerLabDiskV1 Parent, InstallerRuntimePartitionV1 Partition,
    string BeforeSha256, string IntentSha256, string ReadbackSha256);
public sealed record InstallerLabFormatReceiptV1(InstallerLabPartitionReceiptV1 Creation,
    InstallerFileSystemV1 FileSystem, string BeforeSha256, string IntentSha256, string ReadbackSha256);

public static class InstallerLabAcquisition
{
    // Structural correlation of separately observed host and guest facts, not
    // authentication of a caller-supplied JSON document or permission to mutate.
    public static Observation<InstallerLabAcquisitionV1> Correlate(Guid runId, string hostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> created, ImmutableArray<InstallerLabBackingV1> reopenedHost,
        Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(guest);
        if (inventory.Availability != ObservationAvailability.Available)
            return Fail<InstallerLabAcquisitionV1>(inventory.Availability, "LabWholeInventoryUnavailable");
        if (guest.Availability != ObservationAvailability.Available)
            return Fail<InstallerLabAcquisitionV1>(guest.Availability, "LabGuestIdentityUnavailable");
        var valid = InstallerEspBinding.ValidateInventory(inventory.Value);
        if (valid.Availability != ObservationAvailability.Available)
            return Fail<InstallerLabAcquisitionV1>(valid.Availability, valid.Code!);
        if (runId == Guid.Empty || !Hash(hostObservationSha256) || created.IsDefaultOrEmpty || reopenedHost.IsDefault || guest.Value.IsDefault ||
            created.Length != inventory.Value.Disks.Length || guest.Value.Length != created.Length ||
            !created.OrderBy(d => d.Serial, StringComparer.Ordinal).SequenceEqual(reopenedHost.OrderBy(d => d.Serial, StringComparer.Ordinal)) ||
            created.Any(d => !Serial(d.Serial) || d.Length == 0 || d.HostInode == 0 || d.HostDevice == 0 || !Hash(d.CreationSha256) || !Hash(d.LaunchSha256)) ||
            created.Select(d => d.Serial).Distinct(StringComparer.Ordinal).Count() != created.Length ||
            created.Select(d => (d.HostDevice, d.HostInode)).Distinct().Count() != created.Length ||
            guest.Value.Select(d => d.DevicePath).Distinct(StringComparer.Ordinal).Count() != guest.Value.Length ||
            guest.Value.Select(d => d.Serial).Distinct(StringComparer.Ordinal).Count() != guest.Value.Length)
            return Fail<InstallerLabAcquisitionV1>(ObservationAvailability.Ambiguous, "LabBackingOrLaunchChanged");
        var disks = ImmutableArray.CreateBuilder<InstallerLabDiskV1>();
        foreach (var disk in inventory.Value.Disks)
        {
            var observed = guest.Value.SingleOrDefault(g => g.DevicePath == disk.DevicePath);
            var backing = created.SingleOrDefault(b => b.Serial == observed?.Serial);
            if (observed is null || backing is null || backing.Length != disk.SizeBytes ||
                observed.PhysicalSectorSize < disk.LogicalSectorSize || observed.PhysicalSectorSize % disk.LogicalSectorSize != 0)
                return Fail<InstallerLabAcquisitionV1>(ObservationAvailability.Ambiguous, "LabGuestDiskChanged");
            disks.Add(new(backing, disk, observed.PhysicalSectorSize));
        }
        return Observations.Available(new InstallerLabAcquisitionV1(1, runId, hostObservationSha256, disks.ToImmutable()));
    }

    // Only a real before -> one new unformatted partition -> readback transition
    // can supply this LAB receipt. Existing partitions and their filesystems must
    // remain equal. Formatting and production receipt translation are not implied.
    public static Observation<InstallerLabPartitionReceiptV1> VerifyCreation(InstallerLabAcquisitionV1 acquisition,
        Guid generation, InstallerRuntimePartitionV1 intended, Observation<InstallerRuntimeInventoryV1> before,
        Observation<InstallerRuntimeInventoryV1> after, string beforeSha256, string intentSha256, string readbackSha256)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(intended);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Availability != ObservationAvailability.Available || after.Availability != ObservationAvailability.Available)
            return Fail<InstallerLabPartitionReceiptV1>(ObservationAvailability.Unavailable, "LabTransitionObservationUnavailable");
        var b = before.Value; var a = after.Value;
        if (acquisition.SchemaVersion != 1 || acquisition.LabRunId == Guid.Empty || acquisition.Disks.IsDefaultOrEmpty ||
            generation == Guid.Empty || !Hash(beforeSha256) || !Hash(intentSha256) || !Hash(readbackSha256) ||
            InstallerEspBinding.ValidateInventory(b).Availability != ObservationAvailability.Available ||
            InstallerEspBinding.ValidateInventory(a).Availability != ObservationAvailability.Available ||
            !b.Disks.OrderBy(d => d.DevicePath, StringComparer.Ordinal).SequenceEqual(a.Disks.OrderBy(d => d.DevicePath, StringComparer.Ordinal)) ||
            !acquisition.Disks.Select(d => d.Disk).OrderBy(d => d.DevicePath, StringComparer.Ordinal).SequenceEqual(b.Disks.OrderBy(d => d.DevicePath, StringComparer.Ordinal)) ||
            !b.ExternalFileSystems.SequenceEqual(a.ExternalFileSystems) ||
            b.Partitions.Any(p => p.PartitionGuid == intended.PartitionGuid) || a.Partitions.Length != b.Partitions.Length + 1 ||
            b.Partitions.Any(p => !a.Partitions.Contains(p)) || !a.Partitions.Contains(intended) ||
            intended.FileSystem.Availability != ObservationAvailability.Absent)
            return Fail<InstallerLabPartitionReceiptV1>(ObservationAvailability.Ambiguous, "LabCreationTransitionChanged");
        var parent = acquisition.Disks.SingleOrDefault(d => d.Disk.DevicePath == intended.DiskDevicePath);
        return parent is null ? Fail<InstallerLabPartitionReceiptV1>(ObservationAvailability.Ambiguous, "LabParentUnavailable") :
            Observations.Available(new InstallerLabPartitionReceiptV1(acquisition.LabRunId, generation, parent, intended,
                beforeSha256, intentSha256, readbackSha256));
    }

    public static Observation<InstallerLabFormatReceiptV1> VerifyFormat(InstallerLabPartitionReceiptV1 creation,
        Guid generation, InstallerFileSystemV1 expected, Observation<InstallerRuntimeInventoryV1> before,
        Observation<InstallerRuntimeInventoryV1> after, string beforeSha256, string intentSha256, string readbackSha256)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Availability != ObservationAvailability.Available || after.Availability != ObservationAvailability.Available)
            return Fail<InstallerLabFormatReceiptV1>(ObservationAvailability.Unavailable, "LabFormatObservationUnavailable");
        var b = before.Value; var a = after.Value;
        if (creation.GenerationId != generation || generation == Guid.Empty || !Hash(beforeSha256) || !Hash(intentSha256) || !Hash(readbackSha256) ||
            !ValidFileSystem(expected) ||
            InstallerEspBinding.ValidateInventory(b).Availability != ObservationAvailability.Available ||
            InstallerEspBinding.ValidateInventory(a).Availability != ObservationAvailability.Available ||
            !b.Disks.SequenceEqual(a.Disks) || !b.ExternalFileSystems.SequenceEqual(a.ExternalFileSystems) ||
            !b.Disks.Contains(creation.Parent.Disk) || !b.Partitions.Contains(creation.Partition) ||
            creation.Partition.FileSystem.Availability != ObservationAvailability.Absent ||
            b.Partitions.Length != a.Partitions.Length ||
            b.Partitions.Where(p => p.PartitionGuid != creation.Partition.PartitionGuid).Any(p => !a.Partitions.Contains(p)) ||
            !a.Partitions.Contains(creation.Partition with { FileSystem = Observations.Available(expected) }))
            return Fail<InstallerLabFormatReceiptV1>(ObservationAvailability.Ambiguous, "LabFormatTransitionChanged");
        return Observations.Available(new InstallerLabFormatReceiptV1(creation, expected, beforeSha256, intentSha256, readbackSha256));
    }

    private static bool ValidFileSystem(InstallerFileSystemV1 fs) => fs.Type switch
    {
        "EXT4" => Guid.TryParseExact(fs.Uuid, "D", out var id) && id != Guid.Empty,
        "FAT32" => fs.Uuid is { Length: 9 } && fs.Uuid[4] == '-' && fs.Uuid.Where((_, i) => i != 4).All(Uri.IsHexDigit),
        _ => false,
    };

    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool Serial(string value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static Observation<T> Fail<T>(ObservationAvailability availability, string code) => Observations.Failure<T>(availability, code);
}
