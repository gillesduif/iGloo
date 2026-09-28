using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.TestData;

internal static class InstallationOwnershipFixture
{
    internal static (InstallationOwnershipV1 Binding, InstallerRuntimeInventoryV1 Inventory) Create(bool iso = false)
    {
        const ulong mib = PreparationSpacePlanning.Alignment;
        var disk = new CanonicalDiskIdentityV1("eui.fixture", 8, 17, Id(1), 100000 * mib, 512, 4096);
        var windows = new CanonicalVolumeIdentityV1(disk, Id(2), Id(102), PreparationSpacePlanning.EspType, mib, 260 * mib, "FAT32");
        var ntfs = new PreparedGptPartitionV1(disk, Id(3), PreparationSpacePlanning.BasicDataType, 261 * mib, 30000 * mib);
        var plan = new PreparationPlanV1(1, Id(4), disk, windows, [windows.PartitionGuid, ntfs.PartitionGuid],
            PreparationSpacePlanning.Plan(new(40000 * mib, 40000 * mib, 20000 * mib, 1000 * mib, iso ? 5000 * mib : 0, 4096, true)).Value);
        var raw = plan.Space.Allocations.Select((a, i) => new PartitionCreationReceiptV1(plan.GenerationId, a.Role,
            new(disk, Id(10 + i), a.PartitionType, a.OffsetBytes, a.SizeBytes))).ToImmutableArray();
        var formatted = raw.Where(r => r.Role != PreparationRole.LinuxRoot).Select((r, i) => new PreparedPartitionV1(r.Role,
            new(disk, r.Identity.PartitionGuid, Id(110 + i), r.Identity.PartitionType, r.Identity.OffsetBytes, r.Identity.SizeBytes,
                r.Role == PreparationRole.Iso ? "NTFS" : "FAT32"))).ToImmutableArray();
        var esp = formatted.Single(r => r.Role == PreparationRole.LinuxEsp).Identity;
        var payload = formatted.Single(r => r.Role == PreparationRole.Payload).Identity;
        var layout = new PreparedLayoutV1(1, plan, formatted, "3333-CCCC",
            [File(PreparedLayoutRules.ShimPath), File(PreparedLayoutRules.GrubPath), File(@"\EFI\iGloo\grub.cfg")])
        { StorageOwnership = new(plan.GenerationId, [PreparedStorageOwnership.PartitionOf(windows), ntfs], raw) };
        var espBinding = InstallerEspBinding.Declare(layout, A("2222-BBBB"), A("1111-AAAA")).Value;
        var binding = new InstallationOwnershipV1(layout, espBinding, iso ? "ABCD1234ABCD1234" : null);
        var partitions = new List<InstallerRuntimePartitionV1>
        {
            Part(PreparedStorageOwnership.PartitionOf(windows), 1, Fs("FAT32", "1111-AAAA")),
            Part(ntfs, 2, Fs("NTFS", "AAAA1234AAAA1234")),
        };
        foreach (var receipt in raw)
            partitions.Add(Part(receipt.Identity, 11 + (int)receipt.Role, receipt.Role switch
            {
                PreparationRole.LinuxEsp => Fs("FAT32", "2222-BBBB"),
                PreparationRole.Payload => Fs("FAT32", "3333-CCCC"),
                PreparationRole.Iso => Fs("NTFS", "ABCD1234ABCD1234"),
                _ => Observations.Failure<InstallerFileSystemV1>(ObservationAvailability.Absent, "NoRecognizedFilesystemSignature"),
            }));
        return (binding, new([new("/dev/nvme7n3", disk.GptDiskGuid, disk.SizeBytes, disk.LogicalSectorSize)], partitions.ToImmutableArray()));

        CanonicalFileIdentityV1 File(string path) => new(esp.VolumeGuid, path, 100, new string('A', 64));
        static InstallerRuntimePartitionV1 Part(PreparedGptPartitionV1 p, int ordinal, Observation<InstallerFileSystemV1> fs) =>
            new("/dev/nvme7n3p" + ordinal, "/dev/nvme7n3", p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes, fs);
    }

    internal static Observation<T> A<T>(T value) => Observations.Available(value);
    internal static Observation<InstallerFileSystemV1> Fs(string type, string? uuid) => A(new InstallerFileSystemV1(type, uuid));
    internal static Guid Id(int value) => Guid.Parse("00000000-0000-0000-0000-" + value.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
    internal static string Json(InstallerRuntimeInventoryV1 inventory) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, availability = "Available",
        disks = inventory.Disks.Select(d => new { devicePath = d.DevicePath, gptDiskGuid = d.GptDiskGuid, sizeBytes = d.SizeBytes, logicalSectorSize = d.LogicalSectorSize }),
        partitions = inventory.Partitions.Select(p => new { devicePath = p.DevicePath, diskDevicePath = p.DiskDevicePath,
            partitionGuid = p.PartitionGuid, partitionType = p.PartitionType, offsetBytes = p.OffsetBytes, sizeBytes = p.SizeBytes, fileSystem = FsJson(p.FileSystem) }),
        externalFileSystems = inventory.ExternalFileSystems.Select(e => new { devicePath = e.DevicePath, fileSystem = FsJson(e.FileSystem) }),
    });
    private static object FsJson(Observation<InstallerFileSystemV1> fs) => fs.Availability == ObservationAvailability.Available
        ? new { availability = "Available", type = fs.Value.Type, uuid = fs.Value.Uuid }
        : new { availability = fs.Availability.ToString(), code = fs.Code };
}
