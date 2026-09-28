using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class InstallerEspBindingTests
{
    [Fact]
    public void DeclarationRetainsPreparedGenerationAndDistinctEspPayloadIdentities()
    {
        var layout = Layout(); var binding = Declare(layout).Value;
        Assert.Equal(layout.Plan.GenerationId, binding.GenerationId);
        Assert.Equal(layout.Plan.TargetDisk, binding.TargetDisk);
        Assert.Equal(layout.Plan.WindowsEsp, binding.WindowsEsp.Volume);
        Assert.Equal(layout.Partitions.Single(p => p.Role == PreparationRole.LinuxEsp).Identity, binding.LinuxEsp.Volume);
        Assert.Equal("2222-BBBB", binding.LinuxEsp.FileSystemUuid);
        Assert.Equal("3333-CCCC", binding.Payload.FileSystemUuid);
    }

    [Fact]
    public void LinuxEnumerationOrderAndOrdinalsAreNotOwnership()
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding);
        var forward = InstallerEspBinding.Resolve(binding, A(inventory)).Value;
        var reverse = InstallerEspBinding.Resolve(binding, A(inventory with { Partitions = inventory.Partitions.Reverse().ToImmutableArray() })).Value;
        Assert.Equal(forward, reverse);
        Assert.Equal("/dev/nvme7n3p12", forward.LinuxEspDevice);
        Assert.Equal("/dev/nvme7n3p1", forward.WindowsEspDevice);
        var renamed = inventory with
        {
            Disks = [inventory.Disks[0] with { DevicePath = "/dev/sdz" }],
            Partitions = inventory.Partitions.Select((p, i) => p with { DiskDevicePath = "/dev/sdz", DevicePath = "/dev/sdz" + (40 - i) }).ToImmutableArray(),
        };
        Assert.Equal("/dev/sdz39", InstallerEspBinding.Resolve(binding, A(renamed)).Value.LinuxEspDevice);
    }

    [Fact]
    public void MissingOrChangedPartitionGuidCannotFallBackToFirstEsp()
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding);
        Assert.Equal(ObservationAvailability.Absent, InstallerEspBinding.Resolve(binding, A(inventory with { Partitions = inventory.Partitions.RemoveAt(1) })).Availability);
        Assert.Equal(ObservationAvailability.Absent, InstallerEspBinding.Resolve(binding, A(inventory with
        { Partitions = inventory.Partitions.SetItem(1, inventory.Partitions[1] with { PartitionGuid = Guid.NewGuid() }) })).Availability);
    }

    [Theory]
    [InlineData("disk-guid")]
    [InlineData("disk-size")]
    [InlineData("sector-size")]
    [InlineData("partition-type")]
    [InlineData("filesystem")]
    [InlineData("fat16")]
    [InlineData("filesystem-uuid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("parent")]
    public void ChangedIdentityCannotBeTranslated(string change)
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding); var partition = inventory.Partitions[1];
        inventory = change switch
        {
            "disk-guid" => inventory with { Disks = [inventory.Disks[0] with { GptDiskGuid = Guid.NewGuid() }] },
            "disk-size" => inventory with { Disks = [inventory.Disks[0] with { SizeBytes = 123 }] },
            "sector-size" => inventory with { Disks = [inventory.Disks[0] with { LogicalSectorSize = 4096 }] },
            _ => inventory with { Partitions = inventory.Partitions.SetItem(1, change switch
            {
                "partition-type" => partition with { PartitionType = PreparationSpacePlanning.BasicDataType },
                "filesystem" => partition with { FileSystem = A(partition.FileSystem.Value with { Type = "NTFS" }) },
                "fat16" => partition with { FileSystem = A(partition.FileSystem.Value with { Type = "FAT16" }) },
                "filesystem-uuid" => partition with { FileSystem = A(partition.FileSystem.Value with { Uuid = "9999-AAAA" }) },
                "offset" => partition with { OffsetBytes = partition.OffsetBytes + 512 },
                "size" => partition with { SizeBytes = partition.SizeBytes - 512 },
                "parent" => partition with { DiskDevicePath = "/dev/sdb" },
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            }) },
        };
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.Resolve(binding, A(inventory)).Availability);
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("partition")]
    [InlineData("filesystem-uuid")]
    public void DuplicatesAreRejectedBeforeAnyRecipeIsEmitted(string kind)
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding);
        inventory = kind switch
        {
            "disk" => inventory with { Disks = inventory.Disks.Add(inventory.Disks[0] with { DevicePath = "/dev/sdb" }) },
            "partition" => inventory with { Partitions = inventory.Partitions.Add(inventory.Partitions[1] with { DevicePath = "/dev/nvme7n3p19" }) },
            _ => inventory with { Partitions = inventory.Partitions.SetItem(2, inventory.Partitions[2] with { FileSystem = A(new InstallerFileSystemV1("FAT32", "2222-bbbb")) }) },
        };
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.FedoraExistingEspDirective(binding, A(inventory)).Availability);
    }

    [Fact]
    public void WindowsEspAliasAndUnrelatedPayloadAreRejected()
    {
        var binding = Declare(Layout()).Value; var inventory = A(Inventory(binding));
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.Resolve(binding with { LinuxEsp = binding.WindowsEsp }, inventory).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.Resolve(binding with { Payload = binding.LinuxEsp }, inventory).Availability);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("overflow")]
    [InlineData("unaligned")]
    [InlineData("disk-path-alias")]
    public void InvalidUnrelatedInventoryPreventsUniqueTranslation(string defect)
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding);
        var extra = inventory.Partitions[1] with
        {
            DevicePath = "/dev/nvme7n3p13", PartitionGuid = Guid.NewGuid(), FileSystem = A(new InstallerFileSystemV1("FAT32", "4444-DDDD")),
        };
        extra = defect switch
        {
            "overflow" => extra with { OffsetBytes = ulong.MaxValue, SizeBytes = 1024 },
            "unaligned" => extra with { OffsetBytes = 1 },
            "disk-path-alias" => extra with { DevicePath = inventory.Disks[0].DevicePath },
            _ => extra,
        };
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.Resolve(binding,
            A(inventory with { Partitions = inventory.Partitions.Add(extra) })).Availability);
    }

    [Fact]
    public void DistinctGuidsDoNotPermitAnOverlappingBinding()
    {
        var binding = Declare(Layout()).Value;
        var changed = binding with { LinuxEsp = binding.LinuxEsp with
        { Volume = binding.LinuxEsp.Volume with { OffsetBytes = binding.WindowsEsp.Volume.OffsetBytes } } };
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerEspBinding.Resolve(changed, A(Inventory(changed))).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void FailedInventoryAndFilesystemIdentityRemainTyped(ObservationAvailability state)
    {
        var layout = Layout(); var binding = Declare(layout).Value;
        Assert.Equal(state, InstallerEspBinding.Declare(layout, Observations.Failure<string>(state, "probe"), A("1111-AAAA")).Availability);
        Assert.Equal(state, InstallerEspBinding.Resolve(binding, Observations.Failure<InstallerRuntimeInventoryV1>(state, "probe")).Availability);
    }

    [Fact]
    public void FedoraEspDirectiveIsExactNoFormatAndDoesNotSelectWindows()
    {
        var binding = Declare(Layout()).Value;
        var directive = InstallerEspBinding.FedoraExistingEspDirective(binding, A(Inventory(binding))).Value;
        Assert.Equal("part /boot/efi --onpart=UUID=2222-BBBB --noformat\n", directive);
        Assert.DoesNotContain(binding.WindowsEsp.FileSystemUuid, directive, StringComparison.Ordinal);
        Assert.DoesNotContain("autopart", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("clearpart", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("--resize", directive, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("debian", false)]
    [InlineData("debian", true)]
    [InlineData("linuxmint-cinnamon", false)]
    [InlineData("linuxmint-cinnamon", true)]
    public void ValidTwoEspIdentityDoesNotInventPartmanSelectionSupport(string distro, bool reverse)
    {
        var binding = Declare(Layout()).Value; var inventory = Inventory(binding);
        if (reverse) inventory = inventory with { Partitions = inventory.Partitions.Reverse().ToImmutableArray() };
        Assert.Equal(ObservationAvailability.Available, InstallerEspBinding.Resolve(binding, A(inventory)).Availability);
        var support = InstallerEspBinding.PartmanExistingEspDirective(distro);
        Assert.Equal(ObservationAvailability.Unsupported, support.Availability);
        Assert.Throws<InvalidOperationException>(() => support.Value);
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static Observation<InstallerEspBindingV1> Declare(PreparedLayoutV1 layout) =>
        InstallerEspBinding.Declare(layout, A("2222-bbbb"), A("1111-aaaa"));
    private static InstallerRuntimeInventoryV1 Inventory(InstallerEspBindingV1 binding)
    {
        var disk = binding.TargetDisk;
        return new([new("/dev/nvme7n3", disk.GptDiskGuid, disk.SizeBytes, disk.LogicalSectorSize)],
            [Partition(binding.WindowsEsp, "/dev/nvme7n3p1"), Partition(binding.LinuxEsp, "/dev/nvme7n3p12"), Partition(binding.Payload, "/dev/nvme7n3p4")]);
        static InstallerRuntimePartitionV1 Partition(InstallerVolumeBindingV1 v, string path) =>
            new(path, "/dev/nvme7n3", v.Volume.PartitionGuid, v.Volume.PartitionType, v.Volume.OffsetBytes, v.Volume.SizeBytes, A(new InstallerFileSystemV1("FAT32", v.FileSystemUuid)));
    }
    private static PreparedLayoutV1 Layout()
    {
        const ulong mib = PreparationSpacePlanning.Alignment;
        var disk = new CanonicalDiskIdentityV1("eui.test", 8, 17, Guid.NewGuid(), 100000 * mib, 512, 4096);
        var windows = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(), PreparationSpacePlanning.EspType, mib, 260 * mib, "FAT32");
        var plan = new PreparationPlanV1(1, Guid.NewGuid(), disk, windows, [windows.PartitionGuid],
            PreparationSpacePlanning.Plan(new(40000 * mib, 40000 * mib, 20000 * mib, 1000 * mib, 0, 4096, false)).Value);
        var owned = plan.Space.Allocations.Where(a => a.CreateInWindows).Select(a => new PreparedPartitionV1(a.Role,
            new(disk, Guid.NewGuid(), Guid.NewGuid(), a.PartitionType, a.OffsetBytes, a.SizeBytes, a.FileSystem))).ToImmutableArray();
        var esp = owned.Single(p => p.Role == PreparationRole.LinuxEsp).Identity;
        return new(1, plan, owned, "3333-CCCC", [File(PreparedLayoutRules.ShimPath), File(PreparedLayoutRules.GrubPath), File(@"\EFI\iGloo\grub.cfg")]);
        CanonicalFileIdentityV1 File(string path) => new(esp.VolumeGuid, path, 100, new string('A', 64));
    }
}
