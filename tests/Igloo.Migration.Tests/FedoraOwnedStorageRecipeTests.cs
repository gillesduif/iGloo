using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Models;
using Igloo.Distro.FedoraKde;
using System.Text;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class FedoraOwnedStorageRecipeTests
{
    [Fact]
    public async Task ReviewedProviderConfigUsesOnlyOwnedStorageAndNoWifiSecretPath()
    {
        var (binding, inventory) = Reviewed();
        var provider = new FedoraKdePlugin();
        Assert.IsAssignableFrom<IReviewedInstallerStorageProvider>(provider);
        var result = await provider.RenderReviewedInstallerConfigAsync(Manifest(), binding,
            binding.Esp.GenerationId, A(inventory));
        Assert.True(result.Availability == ObservationAvailability.Available, result.Code);
        var text = Encoding.UTF8.GetString(result.Value.Contents.Span);
        Assert.Contains("part / --onpart=PARTUUID=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("autopart --type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("falling back to largest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nmcli device wifi connect", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-storage.cfg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{WIFI_LIST}}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedProviderRefusesUnattendedWifiAndChangedInstallerTarget()
    {
        var (binding, inventory) = Reviewed();
        var provider = new FedoraKdePlugin();
        var wifi = Manifest() with { WifiNetworks = [new WifiNetwork { Ssid = "network", Psk = "private-value" }] };
        var blocked = await provider.RenderReviewedInstallerConfigAsync(wifi, binding,
            binding.Esp.GenerationId, A(inventory));
        Assert.Equal("SecureNetworkCredentialHandoffUnavailable", blocked.Code);
        var wrong = await provider.RenderReviewedInstallerConfigAsync(Manifest(), binding,
            Guid.NewGuid(), A(inventory));
        Assert.NotEqual(ObservationAvailability.Available, wrong.Availability);
        var changed = inventory with { Disks = [inventory.Disks[0] with { GptDiskGuid = Guid.NewGuid() }] };
        var target = await provider.RenderReviewedInstallerConfigAsync(Manifest(), binding,
            binding.Esp.GenerationId, A(changed));
        Assert.NotEqual(ObservationAvailability.Available, target.Availability);
        var noShrinkReceipt = binding with { Layout = binding.Layout with { ShrinkReceipt = null } };
        var missing = await provider.RenderReviewedInstallerConfigAsync(Manifest(), noShrinkReceipt,
            binding.Esp.GenerationId, A(inventory));
        Assert.NotEqual(ObservationAvailability.Available, missing.Availability);
    }

    [Fact]
    public void OptInProviderDoesNotPromoteLegacyOwnershipToReviewedIntent()
    {
        var (legacy, inventory) = Create();
        var provider = new FedoraKdePlugin();
        Assert.NotEqual(ObservationAvailability.Available,
            provider.RenderReviewedStorage(legacy, legacy.Esp.GenerationId, A(inventory)).Availability);
    }

    private static MigrationManifest Manifest() => new()
    {
        DistroId = "fedora-kde",
        User = new() { WindowsUsername = "fixture", PreferredLinuxUsername = "fixture", FullName = "Fixture" },
        Files = new() { StagingPath = "fixture" },
        Hardware = new() { InstallMode = "dual-boot" },
    };

    private static (InstallationOwnershipV1 Binding, InstallerRuntimeInventoryV1 Inventory) Reviewed()
    {
        var (binding, inventory) = Create();
        const ulong mib = PreparationSpacePlanning.Alignment;
        var plan = binding.Layout.Plan;
        var owned = binding.Layout.StorageOwnership!;
        var prior = owned.PreservedPartitions.Single(p => p.PartitionGuid != plan.WindowsEsp.PartitionGuid);
        var sourcePartition = prior with { SizeBytes = 69739 * mib };
        var shrunkPartition = prior with { SizeBytes = 39739 * mib };
        var source = new Igloo.Core.Recovery.CanonicalVolumeIdentityV1(plan.TargetDisk,
            sourcePartition.PartitionGuid, Id(777), sourcePartition.PartitionType,
            sourcePartition.OffsetBytes, sourcePartition.SizeBytes, "NTFS");
        var reviewed = plan with
        {
            ReviewedIntent = new(Id(601), Id(602), Id(603), new string('A', 64),
                new string('B', 64), new(5UL * 1024 * 1024 * 1024, 100 * mib, 100 * mib, 100 * mib), source,
                [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp), sourcePartition],
                30000 * mib, 39739 * mib, 40000 * mib, 40000 * mib),
        };
        var layout = binding.Layout with
        {
            Plan = reviewed,
            ShrinkReceipt = new(reviewed.GenerationId, source,
                source with { SizeBytes = shrunkPartition.SizeBytes }),
            StorageOwnership = owned with
            {
                PreservedPartitions = [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp), shrunkPartition],
            },
        };
        var changed = inventory with
        {
            Partitions = inventory.Partitions.Select(p => p.PartitionGuid == source.PartitionGuid
                ? p with { SizeBytes = shrunkPartition.SizeBytes } : p).ToImmutableArray(),
        };
        return (binding with { Layout = layout }, changed);
    }

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
