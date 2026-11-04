using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedFedoraEspBootTests
{
    [Fact]
    public void FedoraEspConfigRebindsTheExactSeedAndIsCopiedToTheIsoGrubPrefix()
    {
        var stage = Path.Join(Path.GetTempPath(), "igloo-reviewed-fedora-esp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var manifestPath = Path.Join(stage, "migration-manifest.json");
        try
        {
            var disk = new CanonicalDiskIdentityV1("eui.fedora-esp-test", 8, 15,
                Guid.NewGuid(), 40UL * 1024 * 1024 * 1024, 512, 512);
            var linuxEsp = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
                PreparationSpacePlanning.EspType, 1024 * 1024, 1024UL * 1024 * 1024, "FAT32");
            var seed = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
                PreparationSpacePlanning.BasicDataType, 2UL * 1024 * 1024 * 1024,
                2UL * 1024 * 1024 * 1024, "FAT32");
            var generation = Guid.NewGuid();
            var reference = new PreparationCheckpointReference(Guid.NewGuid(), generation, new string('A', 64));
            var storage = new ReviewedInstallerHandoffV1(reference, generation, disk, linuxEsp, seed,
                null, new(generation, disk.GptDiskGuid, linuxEsp.PartitionGuid,
                    4UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024));
            var manifest = new MigrationManifest
            {
                DistroId = "fedora-kde",
                User = new MigrationUser { WindowsUsername = "fixture", PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = stage },
                Hardware = new HardwareProfile
                {
                    InstallMode = "dual-boot", TargetDiskGuid = disk.GptDiskGuid,
                    TargetDiskBytes = (long)disk.SizeBytes, LinuxPartitionSizeGb = 8,
                },
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            File.WriteAllBytes(manifestPath, manifestBytes);
            var bootSpec = new InstallerBootSpec
            {
                MenuTitle = "Install Fedora KDE (Igloo)",
                KernelCmdline = "inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg",
                IsoSha256 = new string('B', 64),
            };
            var content = new ReviewedContentHandoffV1(storage, stage,
                ImmutableArray.Create(new ReviewedStagedFileV1("migration-manifest.json",
                    manifestBytes.Length, Convert.ToHexString(SHA256.HashData(manifestBytes)))),
                manifestBytes.Length, new(false, 0, 0, 0),
                ReviewedPartitionCreationService.HashBootSpec(bootSpec));

            var payload = DirectInstallService.BuildReviewedBootPayload(content, bootSpec);

            Assert.Equal(@"\EFI\BOOT\grub.cfg", payload.LinuxEspCompiledPrefixConfigPath);
            Assert.Contains("probe --set=igloo_esp_partuuid", payload.LinuxEspGrubConfig,
                StringComparison.Ordinal);
            Assert.Contains("configfile ($root)/EFI/BOOT/grub.cfg", payload.LinuxEspGrubConfig,
                StringComparison.Ordinal);
            Assert.Contains(linuxEsp.PartitionGuid.ToString("D"), payload.LinuxEspGrubConfig,
                StringComparison.Ordinal);
            Assert.Contains(seed.PartitionGuid.ToString("D"), payload.LinuxEspGrubConfig,
                StringComparison.Ordinal);
            Assert.Contains("igloo.seedpartuuid=" + seed.PartitionGuid.ToString("D"),
                payload.GrubConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("menuentry", payload.LinuxEspGrubConfig,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(manifestPath);
            Directory.Delete(stage, recursive: false);
        }
    }
}
