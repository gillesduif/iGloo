using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.FedoraKde;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class FedoraOwnedStorageRecipeTests
{
    [Fact]
    public void CompleteRecipeFormatsOnlyOwnedRootAndReusesOnlyOwnedEsp()
    {
        var (binding, inventory) = Create();
        var root = binding.Layout.StorageOwnership!.CreatedPartitions.Single(r => r.Role == PreparationRole.LinuxRoot);
        var recipe = FedoraOwnedStorageRecipe.Generate(binding, binding.Esp.GenerationId, A(inventory)).Value;
        Assert.Equal($"ignoredisk --only-use=nvme7n3\nclearpart --none\nbootloader --boot-drive=nvme7n3\n" +
            $"part /boot/efi --onpart=UUID=2222-BBBB --noformat\npart / --onpart=PARTUUID={root.Identity.PartitionGuid:D} --fstype=ext4\n", recipe);
        Assert.DoesNotContain("autopart", recipe, StringComparison.Ordinal);
        Assert.DoesNotContain("--all", recipe, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.Esp.WindowsEsp.FileSystemUuid, recipe, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.Esp.WindowsEsp.Volume.PartitionGuid.ToString("D"), recipe, StringComparison.Ordinal);
    }

    [Fact]
    public void RecipeIsDeterministicAcrossEnumerationOrder()
    {
        var (binding, inventory) = Create(true);
        var first = FedoraOwnedStorageRecipe.Generate(binding, binding.Esp.GenerationId, A(inventory)).Value;
        var second = FedoraOwnedStorageRecipe.Generate(binding, binding.Esp.GenerationId,
            A(inventory with { Partitions = inventory.Partitions.Reverse().ToImmutableArray() })).Value;
        Assert.Equal(first, second);
        Assert.DoesNotContain("/dev/", first, StringComparison.Ordinal);
    }

    [Fact]
    public void FedoraPayloadTransportUsesOnlyVerifiedUniqueFilesystemUuid()
    {
        var (binding, inventory) = Create();
        var args = FedoraOwnedStorageRecipe.PayloadTransportArguments(binding, binding.Esp.GenerationId, A(inventory)).Value;
        Assert.Equal("inst.stage2=hd:UUID=3333-CCCC: inst.ks=hd:UUID=3333-CCCC:/ks.cfg inst.geoloc=0", args);
        Assert.DoesNotContain("LABEL", args, StringComparison.Ordinal);
        inventory = inventory with { ExternalFileSystems = [new("/dev/loop0", Fs("FAT32", "3333-CCCC"))] };
        Assert.Equal(ObservationAvailability.Ambiguous, FedoraOwnedStorageRecipe.PayloadTransportArguments(binding, binding.Esp.GenerationId, A(inventory)).Availability);
    }

    [Theory]
    [InlineData("missing-esp")]
    [InlineData("duplicate-esp")]
    [InlineData("wrong-uuid")]
    [InlineData("wrong-partuuid")]
    [InlineData("changed-root-extent")]
    [InlineData("unavailable")]
    public void NoRecipeOnChangedOrFailedEvidence(string change)
    {
        var (binding, inventory) = Create();
        var esp = inventory.Partitions.Single(p => p.PartitionGuid == binding.Esp.LinuxEsp.Volume.PartitionGuid);
        var index = inventory.Partitions.IndexOf(esp);
        inventory = change switch
        {
            "missing-esp" => inventory with { Partitions = inventory.Partitions.Remove(esp) },
            "duplicate-esp" => inventory with { Partitions = inventory.Partitions.Add(esp with { DevicePath = "/dev/sdz3" }) },
            "wrong-uuid" => inventory with { Partitions = inventory.Partitions.SetItem(index, esp with { FileSystem = Fs("FAT32", "7777-AAAA") }) },
            "wrong-partuuid" => inventory with { Partitions = inventory.Partitions.SetItem(index, esp with { PartitionGuid = Id(999) }) },
            "changed-root-extent" => inventory with { Partitions = inventory.Partitions.SetItem(inventory.Partitions.Length - 1, inventory.Partitions[^1] with { SizeBytes = 512 }) },
            _ => inventory,
        };
        var observation = change == "unavailable" ? Observations.Failure<InstallerRuntimeInventoryV1>(ObservationAvailability.Unavailable, "probe") : A(inventory);
        var result = FedoraOwnedStorageRecipe.Generate(binding, binding.Esp.GenerationId, observation);
        Assert.NotEqual(ObservationAvailability.Available, result.Availability);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ActualTemplateStorageAllocatorIsReplacedAndNetworkScriptPreserved()
    {
        var (binding, inventory) = Create();
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "kickstart", "ks.cfg.template"));
        var result = FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(template, binding, binding.Esp.GenerationId, A(inventory)).Value;
        Assert.DoesNotContain("echo \"autopart", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback target:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-storage.cfg", result, StringComparison.Ordinal);
        Assert.Contains("nmcli radio wifi on", result, StringComparison.Ordinal);
        Assert.Contains(": > /tmp/ks-network.cfg", result, StringComparison.Ordinal);
        Assert.Contains("%include /tmp/ks-network.cfg", result, StringComparison.Ordinal);
        Assert.Contains("part / --onpart=PARTUUID=", result, StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Ambiguous,
            FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(template + "\nautopart\n", binding, binding.Esp.GenerationId, A(inventory)).Availability);
    }
}
