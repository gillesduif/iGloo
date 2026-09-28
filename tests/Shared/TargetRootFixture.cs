using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Preparation;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.TestData;

internal static class TargetRootFixture
{
    internal sealed record State(InstallationOwnershipV1 Ownership, RootFileSystemReceiptV1 Root,
        InstallerRuntimeInventoryV1 Before, InstallerRuntimeInventoryV1 Inventory,
        TargetRootMountPlanV1 MountPlan, TargetMountReadbackV1 Mounts);

    internal static State Create()
    {
        var (ownership, before) = InstallationOwnershipFixture.Create();
        var rootPartition = ownership.Layout.StorageOwnership!.CreatedPartitions.Single(r => r.Role == PreparationRole.LinuxRoot).Identity;
        var root = new RootFileSystemReceiptV1(ownership.Esp.GenerationId, rootPartition, "EXT4", Id(500));
        var inventory = before with { Partitions = before.Partitions.Select(p => p.PartitionGuid == rootPartition.PartitionGuid
            ? p with { FileSystem = Fs("EXT4", root.FileSystemUuid.ToString("D")) } : p).ToImmutableArray() };
        var numbers = inventory.Disks.Select(d => d.DevicePath).Concat(inventory.Partitions.Select(p => p.DevicePath))
            .Select((p, i) => new LinuxDeviceNumberV1(p, 259, (uint)i)).ToImmutableArray();
        var plan = TargetRootMounts.Declare(root.GenerationId);
        var mountInfo = $"1 0 0:45 / / rw - overlay overlay rw\n" +
            Line(10, 1, rootPartition.PartitionGuid, plan.Root, "ext4", "rw") +
            Line(11, 10, ownership.Esp.LinuxEsp.Volume.PartitionGuid, plan.Esp, "vfat", "rw") +
            Line(12, 1, ownership.Esp.Payload.Volume.PartitionGuid, plan.Payload, "vfat", "ro");
        var mounts = LinuxMountInfo.Parse(mountInfo).Value;
        return new(ownership, root, before, inventory, plan, new(mounts, numbers,
            new[] { plan.Root, plan.Esp, plan.Payload }.Select(p => new TargetPathReadbackV1(p, p, true, false)).ToImmutableArray()));

        string Line(uint id, uint parent, Guid partitionGuid, string path, string fs, string mode)
        {
            var partition = inventory.Partitions.Single(p => p.PartitionGuid == partitionGuid);
            var number = numbers.Single(d => d.DevicePath == partition.DevicePath);
            return FormattableString.Invariant($"{id} {parent} {number.Major}:{number.Minor} / {path} {mode} - {fs} {partition.DevicePath} rw\n");
        }
    }

    internal static InstallationReceiptV1 Receipt(State state, string distro = "debian")
    {
        var esp = state.Ownership.Esp.LinuxEsp.Volume;
        var directory = distro == "debian" ? "debian" : "ubuntu";
        var files = Enum.GetValues<InstalledFileRole>().Select(role => new InstalledFileEvidenceV1(role,
            role is InstalledFileRole.Shim or InstalledFileRole.Grub ? esp.PartitionGuid : state.Root.Partition.PartitionGuid,
            role switch
            {
                InstalledFileRole.Kernel => "/boot/vmlinuz-6.fixture",
                InstalledFileRole.Initramfs => "/boot/initrd.img-6.fixture",
                InstalledFileRole.Fstab => "/etc/fstab",
                InstalledFileRole.GrubConfiguration => "/boot/grub/grub.cfg",
                InstalledFileRole.Shim => $"/EFI/{directory}/shimx64.efi",
                InstalledFileRole.Grub => $"/EFI/{directory}/grubx64.efi",
                InstalledFileRole.Agent => "/opt/igloo/agent.py",
                InstalledFileRole.AgentConfiguration => "/var/lib/igloo/manifest.json",
                _ => "/etc/systemd/system/igloo-first-boot.service",
            }, 99, new string('A', 64))).ToImmutableArray();
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true))
        {
            var path = Encoding.Unicode.GetBytes($"\\EFI\\{directory}\\shimx64.efi\0");
            writer.Write(1U); writer.Write((ushort)(42 + 4 + path.Length + 4));
            writer.Write(Encoding.Unicode.GetBytes("fixture\0"));
            writer.Write(new byte[] { 4, 1, 42, 0 }); writer.Write(99U);
            writer.Write(esp.OffsetBytes / esp.Disk.LogicalSectorSize); writer.Write(esp.SizeBytes / esp.Disk.LogicalSectorSize);
            writer.Write(esp.PartitionGuid.ToByteArray()); writer.Write(new byte[] { 2, 2 });
            writer.Write(new byte[] { 4, 4 }); writer.Write((ushort)(4 + path.Length)); writer.Write(path);
            writer.Write(new byte[] { 0x7f, 0xff, 4, 0 });
        }
        return new(1, state.Ownership, state.Root, distro, distro == "debian" ? "trixie" : "22.3", "unqualified-fixture-v1",
            new("fixture.iso", 999, new string('B', 64), new string('C', 64)),
            Enum.GetValues<DeploymentStage>().Select(s => new DeploymentStageEvidenceV1(s, DeploymentStageOutcome.ReadbackVerified, 0, new string('D', 64))).ToImmutableArray(),
            files, new(9, 7, data.ToArray().ToImmutableArray(), 7, [9, 0, 0, 0]));
    }
}
