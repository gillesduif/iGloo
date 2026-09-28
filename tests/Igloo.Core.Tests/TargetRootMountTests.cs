using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Core.Tests;

public sealed class TargetRootMountTests
{
    [Fact]
    public void FormattedRootRequiresAnOwnedTransitionAndDoesNotWeakenFreshInstall()
    {
        var s = TargetRootFixture.Create();
        Assert.Equal(s.Root, InstallationOwnership.VerifyRootFormat(s.Ownership, s.Root.GenerationId, A(s.Before), A(s.Root), A(s.Inventory)).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, InstallationOwnership.Resolve(s.Ownership, s.Root.GenerationId, A(s.Inventory)).Availability);
        Assert.Equal(ObservationAvailability.Available, Verify(s).Availability);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("partuuid")]
    [InlineData("uuid")]
    [InlineData("filesystem")]
    [InlineData("disk")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void FormattedRootCannotBeSubstituted(string change)
    {
        var s = TargetRootFixture.Create();
        var index = s.Inventory.Partitions.Length - 1;
        var p = s.Inventory.Partitions[index];
        s = change switch
        {
            "generation" => s with { Root = s.Root with { GenerationId = Id(900) } },
            "partuuid" => s with { Root = s.Root with { Partition = s.Root.Partition with { PartitionGuid = Id(900) } } },
            "uuid" => s with { Root = s.Root with { FileSystemUuid = Id(900) } },
            "filesystem" => s with { Root = s.Root with { FileSystem = "XFS" } },
            "disk" => s with { Inventory = s.Inventory with { Disks = [s.Inventory.Disks[0] with { GptDiskGuid = Id(900) }] } },
            "duplicate" => s with { Inventory = s.Inventory with { Partitions = s.Inventory.Partitions.Add(p with { DevicePath = "/dev/sdz9" }) } },
            _ => s with { Inventory = s.Inventory with { Partitions = s.Inventory.Partitions.RemoveAt(index) } },
        };
        Assert.NotEqual(ObservationAvailability.Available, Verify(s).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Absent)]
    public void RootOrMountObservationFailureRemainsTyped(ObservationAvailability state)
    {
        var s = TargetRootFixture.Create();
        Assert.Equal(state, InstallationOwnership.VerifyRootFormat(s.Ownership, s.Root.GenerationId, A(s.Before), A(s.Root),
            Observations.Failure<InstallerRuntimeInventoryV1>(state, "probe")).Availability);
        Assert.Equal(state, TargetRootMounts.Verify(s.Ownership, s.Root, s.MountPlan, A(s.Inventory),
            Observations.Failure<TargetMountReadbackV1>(state, "probe")).Availability);
    }

    [Fact]
    public void FstabAndMappingIgnoreEnumerationAndMountListOrder()
    {
        var s = TargetRootFixture.Create();
        var expected = TargetRootMounts.GenerateFstab(s.Ownership, s.Root, s.MountPlan, A(s.Inventory), A(s.Mounts)).Value;
        var reversed = s with { Inventory = s.Inventory with { Partitions = s.Inventory.Partitions.Reverse().ToImmutableArray() },
            Mounts = s.Mounts with { Mounts = s.Mounts.Mounts.Reverse().ToImmutableArray(), DeviceNumbers = s.Mounts.DeviceNumbers.Reverse().ToImmutableArray() } };
        Assert.Equal(Verify(s).Value, Verify(reversed).Value);
        Assert.Equal(expected, TargetRootMounts.GenerateFstab(reversed.Ownership, reversed.Root, reversed.MountPlan, A(reversed.Inventory), A(reversed.Mounts)).Value);
        Assert.Equal($"UUID={s.Root.FileSystemUuid:D} / ext4 defaults,errors=remount-ro 0 1\nUUID=2222-BBBB /boot/efi vfat umask=0077 0 1\n", expected);
        Assert.DoesNotContain("/dev/", expected, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Ownership.Esp.WindowsEsp.FileSystemUuid, expected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows-as-esp")]
    [InlineData("windows-mounted")]
    [InlineData("subtree")]
    [InlineData("stacked")]
    [InlineData("alias")]
    [InlineData("symlink")]
    [InlineData("resolved-path")]
    [InlineData("propagation")]
    [InlineData("parent")]
    [InlineData("readonly-root")]
    [InlineData("readonly-superblock")]
    [InlineData("writable-payload")]
    [InlineData("changed-plan")]
    public void MountSubstitutionOrExposureFailsClosed(string change)
    {
        var s = TargetRootFixture.Create(); var mounts = s.Mounts.Mounts;
        var windowsPath = s.Inventory.Partitions.Single(p => p.PartitionGuid == s.Ownership.Esp.WindowsEsp.Volume.PartitionGuid).DevicePath;
        var windows = s.Mounts.DeviceNumbers.Single(d => d.DevicePath == windowsPath);
        var changed = change switch
        {
            "windows-as-esp" => mounts.SetItem(2, mounts[2] with { Major = windows.Major, Minor = windows.Minor }),
            "windows-mounted" => mounts.Add(mounts[2] with { MountId = 44, MountPoint = "/elsewhere", Major = windows.Major, Minor = windows.Minor, Options = ["ro"] }),
            "subtree" => mounts.SetItem(1, mounts[1] with { FileSystemRoot = "/subdirectory" }),
            "stacked" => mounts.Add(mounts[2] with { MountId = 44 }),
            "alias" => mounts.Add(mounts[2] with { MountId = 44, MountPoint = "/alias" }),
            "propagation" => mounts.SetItem(0, mounts[0] with { Propagation = ["shared:1"] }),
            "parent" => mounts.SetItem(2, mounts[2] with { ParentId = 99 }),
            "readonly-root" => mounts.SetItem(1, mounts[1] with { Options = ["ro"] }),
            "readonly-superblock" => mounts.SetItem(1, mounts[1] with { SuperOptions = ["ro"] }),
            "writable-payload" => mounts.SetItem(3, mounts[3] with { Options = ["rw"] }),
            _ => mounts,
        };
        s = s with { Mounts = s.Mounts with { Mounts = changed } };
        if (change is "symlink" or "resolved-path") s = s with { Mounts = s.Mounts with { Paths = s.Mounts.Paths.SetItem(1,
            s.Mounts.Paths[1] with { ContainsSymlink = change == "symlink", ResolvedPath = change == "resolved-path" ? "/elsewhere" : s.MountPlan.Esp }) } };
        if (change == "changed-plan") s = s with { MountPlan = s.MountPlan with { Esp = s.MountPlan.Root + "/different" } };
        Assert.Equal(ObservationAvailability.Ambiguous, Verify(s).Availability);
    }

    [Theory]
    [InlineData("1 0 8:1 / / rw - ext4 /dev/sda1 rw\n", ObservationAvailability.Available)]
    [InlineData("1 0 8:1 / /with\\040space rw - ext4 /dev/sda1 rw\n", ObservationAvailability.Available)]
    [InlineData("1 0 8:1 / /bad\\777 rw - ext4 /dev/sda1 rw\n", ObservationAvailability.Ambiguous)]
    [InlineData("1 0 8:1 / / rw ext4 /dev/sda1 rw\n", ObservationAvailability.Ambiguous)]
    [InlineData("999999999999 0 8:1 / / rw - ext4 /dev/sda1 rw\n", ObservationAvailability.Ambiguous)]
    public void MountInfoUsesTheKernelContract(string value, ObservationAvailability expected) =>
        Assert.Equal(expected, LinuxMountInfo.Parse(value).Availability);

    private static Observation<VerifiedTargetMountsV1> Verify(TargetRootFixture.State s) =>
        TargetRootMounts.Verify(s.Ownership, s.Root, s.MountPlan, A(s.Inventory), A(s.Mounts));
}
