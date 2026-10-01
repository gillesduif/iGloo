using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public static class DebianImportJournalStorage
{
    // Fresh hash-pinned observer output, never a claim that tmpfs/fsync is reboot-durable.
    // The installer runtime's separately nominated persistent EXT4 volume is lab evidence;
    // this does not provision a production journal partition or authorize source delivery.
    public static ImmutableArray<DebianJournalStoreWitnessV1> Verify(DebianConfiguredRootImportPlanV1 plan,
        string sessionStore, string importStore, Guid expectedRuntimeFileSystemUuid, JsonElement observed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var inventory = LinuxInstallerInventoryProtocol.Parse(observed.GetProperty("Inventory").GetRawText());
        var numbers = JsonSerializer.Deserialize<ImmutableArray<LinuxDeviceNumberV1>>(observed.GetProperty("DeviceNumbers"));
        var canonical = InstallerBlockLeases.Acquire(plan.Ownership, plan.Root, plan.Root.GenerationId, Guid.NewGuid(),
            DateTimeOffset.UtcNow, inventory, Observations.Available(numbers));
        if (canonical.Availability != ObservationAvailability.Available) throw new InvalidDataException(canonical.Code);
        var protectedDisks = plan.Ownership.Layout.StorageOwnership!.PreservedPartitions
            .Concat(plan.Ownership.Layout.StorageOwnership.CreatedPartitions.Select(p => p.Identity)).Select(p => p.Disk.GptDiskGuid).ToHashSet();
        return VerifyPlacement(sessionStore, importStore, expectedRuntimeFileSystemUuid, observed, inventory, numbers, protectedDisks);
    }

    public static ImmutableArray<DebianJournalStoreWitnessV1> Verify(ValidatedInstallationStorage storage,
        string sessionStore, string importStore, Guid expectedRuntimeFileSystemUuid, JsonElement observed)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var inventory = LinuxInstallerInventoryProtocol.Parse(observed.GetProperty("Inventory").GetRawText());
        var numbers = JsonSerializer.Deserialize<ImmutableArray<LinuxDeviceNumberV1>>(observed.GetProperty("DeviceNumbers"));
        var guest = JsonSerializer.Deserialize<ImmutableArray<InstallerLabGuestDiskV1>>(observed.GetProperty("GuestDisks"));
        var canonical = InstallerBlockLeases.Acquire(storage, Guid.NewGuid(), DateTimeOffset.UtcNow, inventory,
            Observations.Available(numbers), Observations.Available(guest));
        if (canonical.Availability != ObservationAvailability.Available) throw new InvalidDataException(canonical.Code);
        return VerifyPlacement(sessionStore, importStore, expectedRuntimeFileSystemUuid, observed, inventory, numbers,
            storage.Closure.Select(p => p.Disk.GptDiskGuid).ToHashSet());
    }

    private static ImmutableArray<DebianJournalStoreWitnessV1> VerifyPlacement(string sessionStore, string importStore,
        Guid expectedRuntimeFileSystemUuid, JsonElement observed, Observation<InstallerRuntimeInventoryV1> inventory,
        ImmutableArray<LinuxDeviceNumberV1> numbers, HashSet<Guid> protectedDisks)
    {
        var stores = JsonSerializer.Deserialize<ImmutableArray<DebianJournalStoreWitnessV1>>(observed.GetProperty("Stores"));
        if (expectedRuntimeFileSystemUuid == Guid.Empty || stores.Length != 2 || stores[0].Path != sessionStore || stores[1].Path != importStore ||
            sessionStore == importStore || stores[0].Device == stores[1].Device && stores[0].Inode == stores[1].Inode)
            throw new InvalidDataException("Journal store declaration changed.");
        foreach (var store in stores)
        {
            var locator = numbers.SingleOrDefault(n => n.Major == store.Major && n.Minor == store.Minor);
            var partition = inventory.Value.Partitions.SingleOrDefault(p => p.DevicePath == locator?.DevicePath);
            if (store.FileSystem != "EXT4" || store.MountId == 0 || store.Inode == 0 || partition is null ||
                partition.FileSystem.Availability != ObservationAvailability.Available || partition.FileSystem.Value.Type != "EXT4" ||
                !Guid.TryParseExact(partition.FileSystem.Value.Uuid, "D", out var fs) || fs != expectedRuntimeFileSystemUuid ||
                protectedDisks.Contains(inventory.Value.Disks.Single(d => d.DevicePath == partition.DiskDevicePath).GptDiskGuid))
                throw new InvalidDataException("Journal must be on the nominated persistent runtime volume outside protected/target disks.");
        }
        return stores;
    }
}
