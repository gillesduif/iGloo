using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Core.Tests;

public sealed class InstallationOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteOwnershipIncludesUnformattedRootAndOptionalIso(bool iso)
    {
        var (binding, inventory) = Create(iso);
        var root = binding.Layout.StorageOwnership!.CreatedPartitions.Single(r => r.Role == PreparationRole.LinuxRoot);
        Assert.Equal(root, PreparedStorageOwnership.VerifyCreation(binding.Layout.Plan, PreparationRole.LinuxRoot, A(root.Identity), A(root.Identity)).Value);
        Assert.Equal(ObservationAvailability.Available, PreparedStorageOwnership.VerifyStructure(binding.Layout).Availability);
        Assert.Equal(ObservationAvailability.Available, InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory)).Availability);
        Assert.Equal(iso, InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory)).Value.IsoDevice is not null);
    }

    [Theory]
    [InlineData("root-guid")]
    [InlineData("root-extent")]
    [InlineData("root-type")]
    [InlineData("root-formatted")]
    [InlineData("windows")]
    [InlineData("root-alias")]
    [InlineData("disk")]
    [InlineData("duplicate")]
    public void ChangedOwnedOrPreservedStorageFailsClosed(string change)
    {
        var (binding, inventory) = Create();
        var index = inventory.Partitions.Length - 1;
        var root = inventory.Partitions[index];
        inventory = change switch
        {
            "windows" => inventory with { Partitions = inventory.Partitions.SetItem(1, inventory.Partitions[1] with { SizeBytes = 512 }) },
            "disk" => inventory with { Disks = [inventory.Disks[0] with { GptDiskGuid = Id(999) }] },
            "duplicate" => inventory with { Partitions = inventory.Partitions.Add(root with { DevicePath = "/dev/sdz9" }) },
            _ => inventory with { Partitions = inventory.Partitions.SetItem(index, change switch
            {
                "root-guid" => root with { PartitionGuid = Id(999) },
                "root-extent" => root with { SizeBytes = root.SizeBytes - 512 },
                "root-type" => root with { PartitionType = PreparationSpacePlanning.EspType },
                "root-formatted" => root with { FileSystem = Fs("EXT4", "already-installed") },
                _ => root with { PartitionGuid = binding.Esp.WindowsEsp.Volume.PartitionGuid },
            }) },
        };
        Assert.NotEqual(ObservationAvailability.Available, InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory)).Availability);
    }

    [Fact]
    public void ReversedEnumerationProducesSameOwnedMapping()
    {
        var (binding, inventory) = Create();
        var first = InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory)).Value;
        var second = InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory with { Partitions = inventory.Partitions.Reverse().ToImmutableArray() })).Value;
        Assert.Equal(first, second);
        Assert.NotEqual(first.WindowsEspDevice, first.LinuxEspDevice);
        Assert.NotEqual(first.WindowsEspDevice, first.RootDevice);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("receipt-generation")]
    [InlineData("receipt-missing")]
    [InlineData("preserved-missing")]
    [InlineData("root-reserved")]
    [InlineData("raw-volume-mismatch")]
    public void IncompleteOrStaleReceiptsNeverQualify(string change)
    {
        var (binding, inventory) = Create(); var layout = binding.Layout; var owned = layout.StorageOwnership!;
        layout = change switch
        {
            "generation" => layout with { StorageOwnership = owned with { GenerationId = Id(888) } },
            "receipt-generation" => layout with { StorageOwnership = owned with { CreatedPartitions = owned.CreatedPartitions.SetItem(0, owned.CreatedPartitions[0] with { GenerationId = Id(888) }) } },
            "receipt-missing" => layout with { StorageOwnership = owned with { CreatedPartitions = owned.CreatedPartitions.RemoveAt(0) } },
            "preserved-missing" => layout with { StorageOwnership = owned with { PreservedPartitions = owned.PreservedPartitions.RemoveAt(1) } },
            "root-reserved" => layout with { Plan = layout.Plan with { Space = layout.Plan.Space with
                { Allocations = layout.Plan.Space.Allocations.Select(a => a.Role == PreparationRole.LinuxRoot ? a with { CreateInWindows = false } : a).ToImmutableArray() } } },
            _ => layout with { Partitions = layout.Partitions.SetItem(0, layout.Partitions[0] with { Identity = layout.Partitions[0].Identity with { PartitionGuid = Id(888) } }) },
        };
        Assert.NotEqual(ObservationAvailability.Available, InstallationOwnership.Resolve(binding with { Layout = layout }, binding.Esp.GenerationId, A(inventory)).Availability);
    }

    [Fact]
    public void ReopenedGenerationIsAnIndependentPrerequisite()
    {
        var (binding, inventory) = Create();
        Assert.Equal(ObservationAvailability.Ambiguous, InstallationOwnership.Resolve(binding, Id(888), A(inventory)).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void RawPartitionReadbackFailureIsNotAReceipt(ObservationAvailability state)
    {
        var (binding, _) = Create(); var receipt = binding.Layout.StorageOwnership!.CreatedPartitions[0];
        Assert.Equal(state, PreparedStorageOwnership.VerifyCreation(binding.Layout.Plan, receipt.Role, A(receipt.Identity),
            Observations.Failure<PreparedGptPartitionV1>(state, "probe")).Availability);
    }

    [Fact]
    public void StructuredCollectorProtocolRetainsAbsentRootAndExternalMedia()
    {
        var (_, inventory) = Create();
        inventory = inventory with { ExternalFileSystems = [new("/dev/loop0", Fs("SQUASHFS", null))] };
        var read = LinuxInstallerInventoryProtocol.Parse(InstallationOwnershipFixture.Json(inventory)).Value;
        Assert.Equal(ObservationAvailability.Absent, read.Partitions[^1].FileSystem.Availability);
        Assert.Null(read.ExternalFileSystems[0].FileSystem.Value.Uuid);
    }

    [Theory]
    [InlineData("{", ObservationAvailability.Ambiguous)]
    [InlineData("{\"schemaVersion\":2}", ObservationAvailability.Unsupported)]
    [InlineData("{\"schemaVersion\":1,\"availability\":\"AccessDenied\",\"code\":\"probe\"}", ObservationAvailability.AccessDenied)]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", ObservationAvailability.Ambiguous)]
    public void ProtocolNeverDefaultsFailedEvidence(string json, ObservationAvailability state) =>
        Assert.Equal(state, LinuxInstallerInventoryProtocol.Parse(json).Availability);

    [Fact]
    public void ExternalMediaWithSameFilesystemUuidPreventsRecipe()
    {
        var (binding, inventory) = Create();
        inventory = inventory with { ExternalFileSystems = [new("/dev/loop0", Fs("FAT32", binding.Esp.LinuxEsp.FileSystemUuid))] };
        Assert.Equal(ObservationAvailability.Ambiguous, InstallationOwnership.Resolve(binding, binding.Esp.GenerationId, A(inventory)).Availability);
    }
}
