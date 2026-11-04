using FluentAssertions;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Xunit;

namespace Igloo.UsbWriter.Tests;

public sealed class FatSeedRecipeTests
{
    private const string IsoDigest =
        "BD285201494DD0BA09B54D05AC707DE1401668B8512A573EDB5922DCF9D7067E";

    private static InstallerBootSpec FedoraSpec() => new()
    {
        MenuTitle = "Install Fedora KDE (Igloo)",
        IsoSha256 = IsoDigest,
        KernelCmdline =
            "inst.stage2=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}: " +
            "inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg inst.geoloc=0",
        KernelIsoPaths = ["images/pxeboot/vmlinuz"],
        InitrdIsoPaths = ["images/pxeboot/initrd.img"],
        ExtraIsoFiles = [new IsoFileStage("images/install.img", "images/install.img", true)],
    };

    [Fact]
    public void Fedora_fat_seed_requires_pinned_media_and_exact_stage2_and_kickstart()
    {
        var valid = FedoraSpec();
        UsbWriterService.RequireFedoraFatBootSpec(valid, IsoDigest);

        var wrongImage = () => UsbWriterService.RequireFedoraFatBootSpec(valid,
            new string('0', 64));
        wrongImage.Should().Throw<NotSupportedException>();

        var labelLookup = valid with
        {
            KernelCmdline = "inst.stage2=hd:LABEL=OEMDRV: inst.ks=hd:LABEL=OEMDRV:/ks.cfg",
        };
        var ambiguousSeed = () => UsbWriterService.RequireFedoraFatBootSpec(
            labelLookup, IsoDigest);
        ambiguousSeed.Should().Throw<NotSupportedException>();

        var injectedMenu = valid with { MenuTitle = "Fedora\"; halt" };
        var unsafeMenu = () => UsbWriterService.RequireFedoraFatBootSpec(
            injectedMenu, IsoDigest);
        unsafeMenu.Should().Throw<NotSupportedException>();

        var aliasedIsoPath = valid with
        {
            KernelIsoPaths = ["images/pxeboot/vmlinuz."],
        };
        var unsafeAlias = () => UsbWriterService.RequireFedoraFatBootSpec(
            aliasedIsoPath, IsoDigest);
        unsafeAlias.Should().Throw<InvalidDataException>().WithMessage("*unsafe*");
    }

    [Fact]
    public void Mounted_iso_source_requires_one_volume_guid_instead_of_a_reusable_drive_letter()
    {
        const string root = @"\\?\Volume{C5E78564-CA25-11F1-8191-28D043811D67}\";
        UsbWriterService.RequireIsoVolumeRoot(root + "\r\n").Should().Be(root);
        var staleLetter = () => UsbWriterService.RequireIsoVolumeRoot("E:\\");
        staleLetter.Should().Throw<InvalidDataException>();
        var ambiguous = () => UsbWriterService.RequireIsoVolumeRoot(root + "\n" + root);
        ambiguous.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Fat_seed_partition_plan_counts_signed_boot_assets_and_migration_data()
    {
        var drive = new UsbDriveInfo(2, "fixture", 2L * 1024 * 1024 * 1024,
            @"\\.\PHYSICALDRIVE2", "fixture", 512);
        var planned = UsbWriterService.PlanFatSeedPartitionSizeMb(drive,
            stagingBytes: 100L * 1024 * 1024,
            bootBytes: 900L * 1024 * 1024);
        planned.Should().Be(1256);
        var small = () => UsbWriterService.PlanFatSeedPartitionSizeMb(
            drive with { SizeBytes = 1256L * 1024 * 1024 },
            stagingBytes: 100L * 1024 * 1024,
            bootBytes: 900L * 1024 * 1024);
        small.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
        var msrAndGptFit = () => UsbWriterService.PlanFatSeedPartitionSizeMb(
            drive with { SizeBytes = (1256L + 31) * 1024 * 1024 },
            stagingBytes: 100L * 1024 * 1024,
            bootBytes: 900L * 1024 * 1024);
        msrAndGptFit.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
        UsbWriterService.PlanFatSeedPartitionSizeMb(
            drive with { SizeBytes = (1256L + 32) * 1024 * 1024 },
            stagingBytes: 100L * 1024 * 1024,
            bootBytes: 900L * 1024 * 1024).Should().Be(1256);
    }

    [Fact]
    public void Fat_seed_rejects_any_file_too_large_for_fat32_before_diskpart()
    {
        UsbWriterService.ValidateFat32FileSize(uint.MaxValue);
        var oversized = () => UsbWriterService.ValidateFat32FileSize((long)uint.MaxValue + 1);
        oversized.Should().Throw<InvalidOperationException>().WithMessage("*4 GiB*");
        var invalid = () => UsbWriterService.ValidateFat32FileSize(-1);
        invalid.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Many_tiny_selected_files_exceed_fat_capacity_before_diskpart()
    {
        var drive = new UsbDriveInfo(2, "fixture", 1024L * 1024 * 1024,
            @"\\.\PHYSICALDRIVE2", "fixture", 512);
        var tinyFiles = Enumerable.Range(0, 14_000)
            .Select(index => new UsbWriterService.FatSeedFile(
                "source-" + index, "files/tiny-" + index, 1))
            .ToArray();
        var allocated = UsbWriterService.EstimateFat32AllocatedBytes(tinyFiles, 1);
        allocated.Should().BeGreaterThan(850L * 1024 * 1024);
        var plan = () => UsbWriterService.PlanFatSeedPartitionSizeMb(
            drive, allocated, bootBytes: 1);
        plan.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
    }

    [Fact]
    public void Staged_user_paths_reject_fat_aliases_and_component_collisions_before_diskpart()
    {
        static UsbWriterService.FatSeedFile File(string relative) =>
            new("fixture", relative, 1);
        foreach (var path in new[]
        {
            "files/CON.txt", "files/NUL .txt", "files/report. ",
            "files/README~1.TXT",
            "files/" + new string('a', 256),
        })
        {
            var reject = () => UsbWriterService.RequireSafeFatStagingPaths(
                [File(path)], ["files"]);
            reject.Should().Throw<InvalidDataException>();
        }
        var redirectedDirectory = () => UsbWriterService.RequireSafeFatStagingPaths(
            [File("files/normal.txt")], ["files", "files/LPT1"]);
        redirectedDirectory.Should().Throw<InvalidDataException>();
        var sameComponent = () => UsbWriterService.RequireSafeFatStagingPaths(
            [File("files/Alpha/one.txt"), File("files/alpha/two.txt")],
            ["files", "files/Alpha", "files/alpha"]);
        sameComponent.Should().Throw<InvalidDataException>()
            .WithMessage("*component collisions*");
        var normalizedName = () => UsbWriterService.RequireSafeFatStagingPaths(
            [File("files/é.txt"), File("files/e\u0301.txt")], ["files"]);
        normalizedName.Should().Throw<InvalidDataException>()
            .WithMessage("*component collisions*");
        UsbWriterService.RequireSafeFatStagingPaths(
            [File("files/München/Notizen.txt")],
            ["files", "files/München"]);
    }

    [Fact]
    public void Selected_folder_and_browser_profile_must_exist_before_usb_mutation()
    {
        var manifest = new MigrationManifest
        {
            DistroId = "fedora-kde",
            User = new MigrationUser
            {
                WindowsUsername = "lab", PreferredLinuxUsername = "lab",
            },
            Files = new FileMigrationPlan
            {
                StagingPath = "lab",
                IncludedFolders = ["Documents"],
            },
            Browsers = [new BrowserMigration
            {
                Name = "Firefox", SourceRelativePath = "Mozilla/Firefox",
                ProfileStagingPath = "browser-profiles/Mozilla/Firefox",
            }],
            Hardware = new HardwareProfile
            {
                InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = Guid.NewGuid(),
            },
        };
        UsbWriterService.RequireSelectedStagingPaths(manifest,
            [@"files\Documents", @"browser-profiles\Mozilla\Firefox"], []);
        var missingBrowser = () => UsbWriterService.RequireSelectedStagingPaths(manifest,
            [@"files\Documents"], []);
        missingBrowser.Should().Throw<InvalidDataException>().WithMessage("*browser profile*absent*");
        var missingFolder = () => UsbWriterService.RequireSelectedStagingPaths(manifest,
            [@"browser-profiles\Mozilla\Firefox"], []);
        missingFolder.Should().Throw<InvalidDataException>().WithMessage("*folder*absent*");
    }

    [Fact]
    public void Unselected_staged_files_are_rejected_before_usb_mutation()
    {
        static UsbWriterService.FatSeedFile Staged(string relative) =>
            new(relative, relative, 1);
        var manifest = new MigrationManifest
        {
            DistroId = "fedora-kde",
            User = new MigrationUser
            {
                WindowsUsername = "lab", PreferredLinuxUsername = "lab",
            },
            Files = new FileMigrationPlan
            {
                StagingPath = "lab", IncludedFolders = ["Documents"],
            },
            Hardware = new HardwareProfile
            {
                InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = Guid.NewGuid(),
            },
        };
        var selectedDirectories = new[] { "files", "files/Documents", "igloo-agent" };
        var selectedFiles = new[]
        {
            Staged("migration-manifest.json"), Staged("ks.cfg"),
            Staged("igloo-agent/agent.py"), Staged("files/Documents/selected.txt"),
        };
        var providerFiles = new[] { "igloo-agent/agent.py" };
        UsbWriterService.RequireSelectedStagingPaths(manifest, selectedDirectories,
            selectedFiles, providerFiles);
        var unselectedFolder = () => UsbWriterService.RequireSelectedStagingPaths(
            manifest, [.. selectedDirectories, "files/Pictures"],
            [.. selectedFiles, Staged("files/Pictures/private.txt")], providerFiles);
        unselectedFolder.Should().Throw<InvalidDataException>()
            .WithMessage("*unselected*");
        var unselectedRootFile = () => UsbWriterService.RequireSelectedStagingPaths(
            manifest, selectedDirectories, [.. selectedFiles, Staged("private.txt")],
            providerFiles);
        unselectedRootFile.Should().Throw<InvalidDataException>()
            .WithMessage("*unselected*");
    }

    [Fact]
    public void Selected_wallpaper_and_avatar_must_exist_before_usb_mutation()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-selected-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wallpaper = Path.Join(root, "igloo-wallpaper.PNG");
            var avatar = Path.Join(root, "igloo-avatar.jpg");
            File.WriteAllBytes(wallpaper, [1, 2, 3]);
            var manifest = new MigrationManifest
            {
                DistroId = "fedora-kde",
                User = new MigrationUser
                {
                    WindowsUsername = "lab", PreferredLinuxUsername = "lab",
                },
                Files = new FileMigrationPlan
                {
                    StagingPath = root, IncludedFolders = [],
                },
                Hardware = new HardwareProfile
                {
                    InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                    TargetDiskGuid = Guid.NewGuid(),
                },
                Wallpaper = new WallpaperMigration { FileName = "igloo-wallpaper.PNG" },
                AccountPicture = new AccountPictureMigration { FileName = "igloo-avatar.jpg" },
            };
            var files = new[]
            {
                new UsbWriterService.FatSeedFile(wallpaper, "igloo-wallpaper.PNG", 3),
            };
            var missing = () => UsbWriterService.RequireSelectedStagingPaths(manifest, [], files);
            missing.Should().Throw<InvalidDataException>().WithMessage("*image*absent*");

            File.WriteAllBytes(avatar, [4, 5, 6]);
            var selected = UsbWriterService.RequireSelectedStagingPaths(manifest, [],
                [.. files, new UsbWriterService.FatSeedFile(avatar, "igloo-avatar.jpg", 3)]);
            selected.Should().Equal("igloo-wallpaper.PNG", "igloo-avatar.jpg");

            var escaped = () => UsbWriterService.RequireSelectedStagingPaths(
                manifest with { Wallpaper = new WallpaperMigration { FileName = "../outside.png" } },
                [], [.. files, new UsbWriterService.FatSeedFile(avatar, "igloo-avatar.jpg", 3)]);
            escaped.Should().Throw<InvalidDataException>().WithMessage("*unsafe staged file name*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fat_seed_grub_binds_origin_marker_and_anaconda_to_exact_gpt_ids()
    {
        var disk = Guid.Parse("6357B90A-5A64-4818-B701-4A23C52D3153");
        var partition = Guid.Parse("79DC30C5-4945-43DB-9662-552738EA640D");
        var grub = UsbWriterService.BuildFedoraFatSeedGrubConfig(
            FedoraSpec(), disk, partition);

        grub.Should().Contain("set igloo_origin=\"$root\"");
        grub.Should().Contain("search --no-floppy --set=igloo_candidate --file /igloo-seed-identity.json");
        grub.Should().Contain("if [ \"$igloo_candidate\" != \"$igloo_origin\" ]; then");
        grub.Should().Contain("inst.stage2=hd:/dev/disk/by-partuuid/79dc30c5-4945-43db-9662-552738ea640d:");
        grub.Should().Contain("inst.ks=hd:/dev/disk/by-partuuid/79dc30c5-4945-43db-9662-552738ea640d:/ks.cfg");
        grub.Should().Contain("igloo.seeddiskguid=6357b90a-5a64-4818-b701-4a23c52d3153");
        grub.Should().NotContain("LABEL=OEMDRV");
        grub.Should().NotContain("{SEED_PARTUUID}");
    }

    [Fact]
    public async Task Fat_seed_identity_readback_rejects_changed_kickstart_bytes()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-fat-seed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "original kickstart");
            await File.WriteAllTextAsync(Path.Join(root, "migration-manifest.json"), "{}");
            var disk = Guid.NewGuid();
            var partition = Guid.NewGuid();
            var identity = await SeedIdentityWriter.WriteAsync(root, disk, partition);
            await UsbWriterService.VerifyFatSeedIdentityReadBackAsync(root, identity);

            await File.WriteAllTextAsync(Path.Join(root, "ks.cfg"), "changed kickstart");
            var changed = () => UsbWriterService.VerifyFatSeedIdentityReadBackAsync(
                root, identity);
            await changed.Should().ThrowAsync<IOException>()
                .WithMessage("*bound*differs*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validated_staging_hash_must_match_held_source_bytes()
    {
        var path = Path.Join(Path.GetTempPath(),
            "igloo-fat-stage-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            var bytes = Encoding.ASCII.GetBytes("target-bound Fedora kickstart");
            await File.WriteAllBytesAsync(path, bytes);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            using var held = await UsbWriterService.OpenVerifiedStagingFileAsync(path, digest);
            held.Position.Should().Be(0);
            held.Length.Should().Be(bytes.Length);

            var staleReview = () => UsbWriterService.OpenVerifiedStagingFileAsync(
                path, new string('0', 64));
            await staleReview.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*changed after target review*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Selected_user_file_is_held_before_usb_partition_mutation()
    {
        var stage = Path.Join(Path.GetTempPath(),
            "igloo-fat-user-stage-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Join(stage, "files", "Documents");
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Join(folder, "selected.txt");
            await File.WriteAllTextAsync(source, "selected user bytes");
            var files = UsbWriterService.GetStagingFiles(stage);
            var held = UsbWriterService.OpenReadLockedStagingFiles(stage, files,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            await using var lease = held.ConfigureAwait(false);
            held.TryGetStream(@"files\Documents\selected.txt", out var stream)
                .Should().BeTrue();
            stream.Length.Should().Be(new FileInfo(source).Length);
            var changed = () => File.WriteAllText(source, "changed during USB write");
            changed.Should().Throw<IOException>();
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_artifacts_are_complete_unique_and_pinned_before_disk_mutation()
    {
        var stage = Path.Join(Path.GetTempPath(),
            "igloo-provider-stage-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Join(stage, "igloo-agent");
        Directory.CreateDirectory(agent);
        try
        {
            var agentPath = Path.Join(agent, "agent.py");
            var displayPath = Path.Join(agent, "display-apply.py");
            var agentBytes = Encoding.ASCII.GetBytes("print('first boot')\n");
            var displayBytes = Encoding.ASCII.GetBytes("print('display')\n");
            await File.WriteAllBytesAsync(agentPath, agentBytes);
            await File.WriteAllBytesAsync(displayPath, displayBytes);
            var expected = ImmutableArray.Create(
                new UsbStagedArtifactIdentity("igloo-agent/agent.py",
                    Convert.ToHexString(SHA256.HashData(agentBytes))),
                new UsbStagedArtifactIdentity("igloo-agent/display-apply.py",
                    Convert.ToHexString(SHA256.HashData(displayBytes))));
            var files = UsbWriterService.GetStagingFiles(stage);

            var missing = () => UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, ImmutableArray<UsbStagedArtifactIdentity>.Empty);
            await missing.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*identities are unavailable*");
            var partial = () => UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, expected.RemoveAt(1));
            await partial.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*omits an agent file*");
            var duplicate = () => UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, expected.Add(expected[0] with
                {
                    RelativePath = "IGLOO-AGENT/AGENT.PY",
                }));
            await duplicate.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*duplicate or unexpected path*");
            var unsafePath = () => UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, expected.SetItem(0, expected[0] with
                {
                    RelativePath = "../agent.py",
                }));
            await unsafePath.Should().ThrowAsync<InvalidDataException>();
            var stale = () => UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, expected.SetItem(0, expected[0] with
                {
                    Sha256 = new string('0', 64),
                }));
            await stale.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*changed after target review*");

            await using var held = await UsbWriterService.OpenVerifiedProviderArtifactsAsync(
                stage, files, expected);
            held.TryGetStream(@"igloo-agent\agent.py", out var stream).Should().BeTrue();
            stream.Length.Should().Be(agentBytes.Length);
            var changedDuringCopy = () => File.WriteAllText(agentPath, "tampered");
            changedDuringCopy.Should().Throw<IOException>();
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_seed_agent_snapshot_requires_exact_copy_and_inventory()
    {
        var root = Path.Join(Path.GetTempPath(),
            "igloo-raw-agent-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Join(root, "stage");
        var seed = Path.Join(root, "seed");
        Directory.CreateDirectory(Path.Join(stage, "igloo-agent"));
        Directory.CreateDirectory(Path.Join(seed, "igloo-agent"));
        try
        {
            var source = Path.Join(stage, "igloo-agent", "agent.py");
            var copied = Path.Join(seed, "igloo-agent", "agent.py");
            await File.WriteAllTextAsync(source, "print('bound agent')");
            var held = await UsbWriterService.OpenObservedAgentArtifactsAsync(stage);
            await using var lease = held.ConfigureAwait(false);
            await File.WriteAllTextAsync(copied, "print('bound agent')");
            await UsbWriterService.VerifyProviderAgentCopyAsync(stage, seed, held);

            await File.WriteAllTextAsync(copied, "print('changed agent')");
            await Assert.ThrowsAsync<IOException>(() =>
                UsbWriterService.VerifyProviderAgentCopyAsync(stage, seed, held));
            await File.WriteAllTextAsync(copied, "print('bound agent')");
            await File.WriteAllTextAsync(Path.Join(seed, "igloo-agent", "extra.py"),
                "print('unbound')");
            await Assert.ThrowsAsync<IOException>(() =>
                UsbWriterService.VerifyProviderAgentCopyAsync(stage, seed, held));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
