using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.TestData;

internal static class LabStorageFixture
{
    internal static InstallerLabStorageEvidenceV1 Create(bool import = false)
    {
        var version = import ? 2 : 1; var scope = import ? "ConfiguredRootImport" : "StorageSmoke";
        var run = Guid.NewGuid(); var generation = Guid.NewGuid();
        var disk = new InstallerRuntimeDiskV1("/dev/vdb", Guid.NewGuid(), 40UL << 30, 512);
        var backing = new InstallerLabBackingV1("IGLOO-LAB-TARGET", disk.SizeBytes, 1, 2, new('A', 64), new('B', 64));
        var preserved = new InstallerRuntimePartitionV1("/dev/vdb1", disk.DevicePath, Guid.NewGuid(), PreparationSpacePlanning.EspType,
            1UL << 20, 256UL << 20, Observations.Available(new InstallerFileSystemV1("FAT32", "AAAA-BBBB")));
        var journalDisk = new InstallerRuntimeDiskV1("/dev/vdc", Guid.NewGuid(), 8UL << 30, 512);
        var journalBacking = new InstallerLabBackingV1("IGLOO-LAB-JOURNAL", journalDisk.SizeBytes, 1, 3, new('A', 64), new('B', 64));
        var journalPart = new InstallerRuntimePartitionV1("/dev/vdc1", journalDisk.DevicePath, Guid.NewGuid(), PreparationSpacePlanning.LinuxDataType,
            1UL << 20, 7UL << 30, Observations.Available(new InstallerFileSystemV1("EXT4", Guid.NewGuid().ToString("D"))));
        var inventory = new InstallerRuntimeInventoryV1([disk, journalDisk], [preserved, journalPart]);
        var transitions = ImmutableArray.CreateBuilder<InstallerLabTransitionV1>();
        ulong offset = 257UL << 20;
        foreach (var (role, index) in new[] { (PreparationRole.LinuxEsp, 2), (PreparationRole.Payload, 3), (PreparationRole.LinuxRoot, 4) })
        {
            var type = role == PreparationRole.LinuxEsp ? PreparationSpacePlanning.EspType :
                role == PreparationRole.Payload ? PreparationSpacePlanning.BasicDataType : PreparationSpacePlanning.LinuxDataType;
            var p = new InstallerRuntimePartitionV1("/dev/vdb" + index, disk.DevicePath, Guid.NewGuid(), type, offset, 1UL << 30,
                Observations.Failure<InstallerFileSystemV1>(ObservationAvailability.Absent, "NoFilesystem"));
            var fs = new InstallerFileSystemV1(role == PreparationRole.LinuxRoot ? "EXT4" : "FAT32",
                role == PreparationRole.LinuxRoot ? Guid.NewGuid().ToString("D") : $"1234-000{index}");
            var created = inventory with { Partitions = inventory.Partitions.Add(p) };
            var formatted = created with { Partitions = created.Partitions.Replace(p, p with { FileSystem = Observations.Available(fs) }) };
            var intended = new InstallerLabPartitionIntentV1(p.DevicePath, p.DiskDevicePath, p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes);
            transitions.Add(new(role, intended, Wire(inventory), Wire(created), Wire(formatted), fs,
                JsonSerializer.Serialize(new InstallerLabCreationIntentV1(version, run, generation, role, intended, Hash(Wire(inventory))) { Scope = import ? scope : null }),
                JsonSerializer.Serialize(new InstallerLabFormatIntentV1(version, run, generation, p.PartitionGuid, fs, Hash(Wire(created))) { Scope = import ? scope : null })));
            inventory = formatted; offset += p.SizeBytes;
        }
        return new(version, "IsolatedFileBackedLab", scope, run, generation, new('E', 64),
            [backing, journalBacking], [backing, journalBacking], [new(disk.DevicePath, backing.Serial, 512), new(journalDisk.DevicePath, journalBacking.Serial, 512)], transitions.ToImmutable());
    }

    internal static ValidatedInstallationStorage Validate(InstallerLabStorageEvidenceV1 e) => InstallationStorage.VerifyLab(e,
        LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted), Observations.Available(e.GuestDisks)).Value;

    internal static ImmutableArray<LinuxDeviceNumberV1> Numbers(InstallerRuntimeInventoryV1 i) =>
        i.Disks.Select(d => d.DevicePath).Concat(i.Partitions.Select(p => p.DevicePath))
            .Select((p, n) => new LinuxDeviceNumberV1(p, 252, (uint)n)).ToImmutableArray();

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    internal static string Wire(InstallerRuntimeInventoryV1 i) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, availability = "Available",
        disks = i.Disks.Select(d => new { devicePath = d.DevicePath, gptDiskGuid = d.GptDiskGuid, sizeBytes = d.SizeBytes, logicalSectorSize = d.LogicalSectorSize }),
        partitions = i.Partitions.Select(p => new { devicePath = p.DevicePath, diskDevicePath = p.DiskDevicePath,
            partitionGuid = p.PartitionGuid, partitionType = p.PartitionType, offsetBytes = p.OffsetBytes, sizeBytes = p.SizeBytes,
            fileSystem = p.FileSystem.Availability == ObservationAvailability.Available
                ? (object)new { availability = "Available", type = p.FileSystem.Value.Type, uuid = p.FileSystem.Value.Uuid }
                : new { availability = p.FileSystem.Availability.ToString(), code = p.FileSystem.Code } }),
        externalFileSystems = Array.Empty<object>(),
    });
}
