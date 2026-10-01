using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

public enum InstallerBlockRole { Root, LinuxEsp, Payload }
public enum InstallerBlockAccess { ReadOnly, ReadWrite }
public sealed record InstallerBlockBindingV1(InstallerBlockRole Role, PreparedGptPartitionV1? Partition,
    string FileSystem, string FileSystemUuid, InstallerBlockAccess Access, LinuxDeviceNumberV1 Locator,
    string CanonicalSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstallationPartitionV1? StoragePartition { get; init; }
}

// This is canonical authorization to acquire descriptors, NOT a descriptor or proof of an
// effective lock. Only the trusted native supervisor opens blocks. No deserialization constructor.
public sealed class InstallerBlockLeaseSet
{
    internal InstallerBlockLeaseSet(Guid session, Guid generation, DateTimeOffset acquired,
        ImmutableArray<InstallerBlockBindingV1> bindings, string protectedState)
    {
        SessionId = session;
        GenerationId = generation;
        AcquiredAtUtc = acquired;
        Bindings = bindings;
        ProtectedStateSha256 = protectedState;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstallationStorageProvenanceV1? Provenance { get; internal init; }
    public Guid SessionId { get; }
    public Guid GenerationId { get; }
    public DateTimeOffset AcquiredAtUtc { get; }
    public ImmutableArray<InstallerBlockBindingV1> Bindings { get; }
    public string ProtectedStateSha256 { get; }
}

public static class InstallerBlockLeases
{
    public static Observation<InstallerBlockLeaseSet> Acquire(InstallationOwnershipV1 ownership,
        RootFileSystemReceiptV1 root, Guid generation, Guid session, DateTimeOffset acquiredAtUtc,
        Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<LinuxDeviceNumberV1>> deviceNumbers)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(deviceNumbers);
        if (session == Guid.Empty || acquiredAtUtc.Offset != TimeSpan.Zero || acquiredAtUtc == default)
            return Fail("InvalidLeaseSession");
        var resolved = InstallationOwnership.ResolveFormattedRoot(ownership, generation, root, inventory);
        if (resolved.Availability != ObservationAvailability.Available) return Fail(resolved.Code!, resolved.Availability);
        if (deviceNumbers.Availability != ObservationAvailability.Available) return Fail("BlockStatUnavailable", deviceNumbers.Availability);
        var numbers = deviceNumbers.Value;
        var observed = inventory.Value;
        var paths = observed.Disks.Select(d => d.DevicePath).Concat(observed.Partitions.Select(p => p.DevicePath))
            .Concat(observed.ExternalFileSystems.Select(p => p.DevicePath)).Order(StringComparer.Ordinal);
        if (numbers.IsDefaultOrEmpty || numbers.Any(n => n is null) || !numbers.Select(n => n.DevicePath).Order(StringComparer.Ordinal).SequenceEqual(paths, StringComparer.Ordinal) ||
            numbers.Any(n => n.Major == 0) || numbers.Select(n => (n.Major, n.Minor)).Distinct().Count() != numbers.Length)
            return Fail("IncompleteOrAliasedBlockStat");
        var bindings = ImmutableArray.CreateBuilder<InstallerBlockBindingV1>();
        foreach (var (role, path, preparationRole) in new[]
        {
            (InstallerBlockRole.Root, resolved.Value.RootDevice, PreparationRole.LinuxRoot),
            (InstallerBlockRole.LinuxEsp, resolved.Value.LinuxEspDevice, PreparationRole.LinuxEsp),
            (InstallerBlockRole.Payload, resolved.Value.PayloadDevice, PreparationRole.Payload),
        })
        {
            var partition = ownership.Layout.StorageOwnership!.CreatedPartitions.Single(p => p.Role == preparationRole).Identity;
            var fs = observed.Partitions.Single(p => p.DevicePath == path).FileSystem.Value;
            var access = role == InstallerBlockRole.Root ? InstallerBlockAccess.ReadWrite : InstallerBlockAccess.ReadOnly;
            var fsUuid = fs.Uuid!.ToUpperInvariant();
            bindings.Add(new(role, partition, fs.Type, fsUuid, access, numbers.Single(n => n.DevicePath == path),
                Hash(new { GenerationId = generation, Role = role, Partition = partition, FileSystem = fs.Type, FileSystemUuid = fsUuid, Access = access })));
        }
        var protectedPartitions = ownership.Layout.StorageOwnership!.PreservedPartitions;
        foreach (var partition in protectedPartitions)
        {
            var fs = observed.Partitions.Single(p => p.PartitionGuid == partition.PartitionGuid).FileSystem;
            if (fs.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent))
                return Fail("PreservedFilesystemObservationIncomplete", fs.Availability);
        }
        var protectedState = Hash(protectedPartitions.OrderBy(p => p.PartitionGuid).Select(p => new
        {
            Partition = p,
            FileSystem = observed.Partitions.Single(o => o.PartitionGuid == p.PartitionGuid).FileSystem,
        }).ToArray());
        return Observations.Available(new InstallerBlockLeaseSet(session, generation, acquiredAtUtc, bindings.ToImmutable(), protectedState));
    }

    public static Observation<bool> Revalidate(InstallerBlockLeaseSet leases, InstallationOwnershipV1 ownership,
        RootFileSystemReceiptV1 root, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<LinuxDeviceNumberV1>> deviceNumbers)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var fresh = Acquire(ownership, root, leases.GenerationId, leases.SessionId, leases.AcquiredAtUtc, inventory, deviceNumbers);
        if (fresh.Availability != ObservationAvailability.Available) return Observations.Failure<bool>(fresh.Availability, fresh.Code!);
        if (fresh.Value.ProtectedStateSha256 != leases.ProtectedStateSha256 ||
            !fresh.Value.Bindings.Select(b => b.CanonicalSha256).SequenceEqual(leases.Bindings.Select(b => b.CanonicalSha256)))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "CanonicalBlockLeaseChanged");
        // Renumbering need not change canonical identity, but an existing open descriptor must
        // never silently become a new lease. Restart the session before opening replacement FDs.
        if (!fresh.Value.Bindings.Select(b => b.Locator).SequenceEqual(leases.Bindings.Select(b => b.Locator)))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "BlockLocatorChangedSessionRestartRequired");
        return Observations.Available(true);
    }

    public static Observation<InstallerBlockLeaseSet> Acquire(ValidatedInstallationStorage storage,
        Guid session, DateTimeOffset acquiredAtUtc, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<LinuxDeviceNumberV1>> deviceNumbers,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(deviceNumbers);
        if (session == Guid.Empty || acquiredAtUtc.Offset != TimeSpan.Zero || acquiredAtUtc == default)
            return Fail("InvalidLeaseSession");
        var verified = InstallationStorage.Revalidate(storage, inventory, guest);
        if (verified.Availability != ObservationAvailability.Available) return Fail(verified.Code!, verified.Availability);
        if (deviceNumbers.Availability != ObservationAvailability.Available) return Fail("BlockStatUnavailable", deviceNumbers.Availability);
        var numbers = deviceNumbers.Value;
        var observed = inventory.Value;
        var paths = observed.Disks.Select(d => d.DevicePath).Concat(observed.Partitions.Select(p => p.DevicePath))
            .Concat(observed.ExternalFileSystems.Select(p => p.DevicePath)).Order(StringComparer.Ordinal);
        if (numbers.IsDefaultOrEmpty || numbers.Any(n => n is null) ||
            !numbers.Select(n => n.DevicePath).Order(StringComparer.Ordinal).SequenceEqual(paths, StringComparer.Ordinal) ||
            numbers.Any(n => n.Major == 0) || numbers.Select(n => (n.Major, n.Minor)).Distinct().Count() != numbers.Length)
            return Fail("IncompleteOrAliasedBlockStat");
        var bindings = ImmutableArray.CreateBuilder<InstallerBlockBindingV1>();
        foreach (var (role, index) in new[] { (InstallerBlockRole.Root, 2), (InstallerBlockRole.LinuxEsp, 0), (InstallerBlockRole.Payload, 1) })
        {
            var receipt = storage.Receipts[index];
            var partition = InstallationStorage.Project(receipt.Creation.Partition, observed);
            var access = role == InstallerBlockRole.Root ? InstallerBlockAccess.ReadWrite : InstallerBlockAccess.ReadOnly;
            var fs = receipt.FileSystem;
            var hash = Hash(new { storage.Provenance, Role = role, Partition = partition, FileSystem = fs, Access = access });
            bindings.Add(new(role, null, fs.Type, fs.Uuid!.ToUpperInvariant(), access,
                numbers.Single(n => n.DevicePath == receipt.Creation.Partition.DevicePath), hash) { StoragePartition = partition });
        }
        return Observations.Available(new InstallerBlockLeaseSet(session, storage.Provenance.GenerationId, acquiredAtUtc,
            bindings.ToImmutable(), Hash(new { storage.Provenance, storage.Preserved })) { Provenance = storage.Provenance });
    }

    public static Observation<bool> Revalidate(InstallerBlockLeaseSet leases, ValidatedInstallationStorage storage,
        Observation<InstallerRuntimeInventoryV1> inventory, Observation<ImmutableArray<LinuxDeviceNumberV1>> deviceNumbers,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var fresh = Acquire(storage, leases.SessionId, leases.AcquiredAtUtc, inventory, deviceNumbers, guest);
        if (fresh.Availability != ObservationAvailability.Available) return Observations.Failure<bool>(fresh.Availability, fresh.Code!);
        if (leases.Provenance != fresh.Value.Provenance || leases.ProtectedStateSha256 != fresh.Value.ProtectedStateSha256 ||
            !leases.Bindings.SequenceEqual(fresh.Value.Bindings))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "CanonicalBlockLeaseChanged");
        return Observations.Available(true);
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static Observation<InstallerBlockLeaseSet> Fail(string code, ObservationAvailability state = ObservationAvailability.Ambiguous) =>
        Observations.Failure<InstallerBlockLeaseSet>(state, code);
}
