using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Models;
using Igloo.Distro.FedoraKde;
using System.Text;
using System.Text.Json;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class FedoraOwnedStorageRecipeTests
{
    private static readonly Guid TargetGuid = Guid.Parse("5932e3bb-6b03-4f34-9092-5596eb3a770b");

    [Fact]
    public void FedoraCatalogueIncludesHistoricalDualBootWithoutClaimingReviewedRootBinding()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fedora-kde", "distro.json");
        var declared = JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(path));
        Assert.NotNull(declared);
        Assert.True(declared.IsAvailable);
        Assert.Equal(["dual-boot", "replace"], declared.SupportedInstallModes);

        var provider = new FedoraKdePlugin();
        Assert.False(provider.GetInstallerBootSpec().PreCreateRootPartition);
        Assert.IsNotAssignableFrom<IReviewedInstallerConfigProvider>(provider);
    }

    [Fact]
    public void FedoraDirectBootUsesTheBoundSeedPartitionForKickstartAndStageTwo()
    {
        var spec = new FedoraKdePlugin().GetInstallerBootSpec();
        Assert.Contains("inst.stage2=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:",
            spec.KernelCmdline, StringComparison.Ordinal);
        Assert.Contains("inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg",
            spec.KernelCmdline, StringComparison.Ordinal);
        Assert.DoesNotContain("LABEL=", spec.KernelCmdline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltFedoraPluginStagesRunnableFirstBootAndDisplayPayload()
    {
        var plugin = new FedoraKdePlugin();
        Assert.Matches("^[0-9a-f]{64}$", plugin.Metadata.IsoSha256);
        Assert.True(plugin.Metadata.MinimumRequirements.RequiresUefi);
        var payload = await plugin.GetAgentPayloadAsync();
        var files = payload.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var name in new[] { "first-boot.sh", "agent.py", "display-apply.py", "igloo_boot.py" })
        {
            Assert.True(files.TryGetValue(name, out var file), $"Missing {name} from Fedora payload");
            if (name != "igloo_boot.py")
                Assert.True(file.Executable, $"{name} must be executable");
            Assert.NotEmpty(file.Contents.ToArray());
        }
        Assert.StartsWith("#!/usr/bin/env bash\n", Encoding.UTF8.GetString(files["first-boot.sh"].Contents.Span),
            StringComparison.Ordinal);
        Assert.Contains("def fedora_version()", Encoding.UTF8.GetString(files["agent.py"].Contents.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFedoraConfigRefusesMissingOrInvalidDestructiveTarget()
    {
        var plugin = new FedoraKdePlugin();
        var valid = Manifest() with { Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024, TargetDiskGuid = TargetGuid } };
        // v0.2-alpha behaviour restored 26 Oct 2026: dual boot is the Community default
        // and renders a kickstart; only an invalid destructive target is refused.
        var dualBoot = await plugin.RenderInstallerConfigAsync(Manifest());
        Assert.NotNull(dualBoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
            valid with { Hardware = valid.Hardware with { TargetDiskBytes = 0 } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
            valid with { Hardware = valid.Hardware with { TargetDiskGuid = null } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
            valid with { Hardware = valid.Hardware with { TargetDiskGuid = Guid.Empty } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
            valid with { Hardware = valid.Hardware with { InstallMode = "unknown" } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
            valid with { DistroId = "linuxmint-cinnamon" }));
    }

    [Fact]
    public async Task NvidiaUnderSecureBootStopsBeforeKickstartOrModuleChanges()
    {
        var plugin = new FedoraKdePlugin();
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = true, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "nvidia",
            TotalRamBytes = 8L * 1024 * 1024 * 1024, Findings = [],
        };
        Assert.Contains(plugin.CheckCompatibility(report), finding =>
            finding.Code == "FEDORA_NVIDIA_SECURE_BOOT_UNSUPPORTED" &&
            finding.Severity == FindingSeverity.Warning);

        var manifest = Manifest() with
        {
            Hardware = new()
            {
                InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = TargetGuid, GpuVendor = "nvidia", SecureBootEnabled = true,
            },
        };
        // The legacy (alpha) kickstart renders; the first-boot agent owns the NVIDIA step.
        var legacy = await plugin.RenderInstallerConfigAsync(manifest);
        Assert.Equal("ks.cfg", legacy.FileName);

        var (binding, inventory) = Reviewed();
        var reviewedManifest = Manifest() with
        {
            Hardware = Manifest().Hardware with { GpuVendor = "nvidia", SecureBootEnabled = true },
        };
        var reviewed = await plugin.RenderReviewedInstallerConfigAsync(reviewedManifest,
            binding, binding.Esp.GenerationId, A(inventory));
        Assert.Equal(ObservationAvailability.Unsupported, reviewed.Availability);
        Assert.Equal("FedoraNvidiaSecureBootUnsupported", reviewed.Code);
    }

    [Fact]
    public void LowMemoryFindingProvidesAnActionableRemediation()
    {
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "other",
            TotalRamBytes = 2L * 1024 * 1024 * 1024, Findings = [],
        };
        var finding = Assert.Single(new FedoraKdePlugin().CheckCompatibility(report),
            finding => finding.Code == "RAM_BELOW_RECOMMENDED");
        Assert.Contains("Add enough memory", finding.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FedoraConfigSeparatesSelectedGeckoAndChromiumProfileCopies()
    {
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024, TargetDiskGuid = TargetGuid },
            Browsers =
            [
                new BrowserMigration { Name = "Mozilla Firefox", Engine = "gecko",
                    SourceRelativePath = "AppData/Roaming/Mozilla/Firefox", DestRelativePath = ".mozilla/firefox",
                    ProfileStagingPath = "browser-profiles/AppData/Roaming/Mozilla/Firefox" },
                new BrowserMigration { Name = "Google Chrome", Engine = "chromium",
                    SourceRelativePath = "AppData/Local/Google/Chrome/User Data", DestRelativePath = ".wrong-gecko-destination",
                    ProfileStagingPath = "browser-profiles/AppData/Local/Google/Chrome/User Data" },
            ],
        };
        var config = await new FedoraKdePlugin().RenderInstallerConfigAsync(manifest);
        var kickstart = Encoding.UTF8.GetString(config.Contents.Span);
        Assert.Contains("\ncmdline\n", kickstart, StringComparison.Ordinal);
        Assert.Contains("url --metalink=\"https://mirrors.fedoraproject.org/metalink?repo=fedora-44&arch=x86_64\"",
            kickstart, StringComparison.Ordinal);
        Assert.Contains("TARGET_DISK_GUID=\"5932e3bb-6b03-4f34-9092-5596eb3a770b\"", kickstart, StringComparison.Ordinal);
        Assert.Contains("blkid -p -s PTUUID -o value", kickstart, StringComparison.Ordinal);
        Assert.Contains("$GUID_MATCHES\" -ne 1", kickstart, StringComparison.Ordinal);
        Assert.Contains("blkid -t LABEL=OEMDRV -o device", kickstart, StringComparison.Ordinal);
        Assert.Contains("python3 /tmp/igloo-validate-seed.py", kickstart, StringComparison.Ordinal);
        Assert.Contains("igloo.seedpartuuid", kickstart, StringComparison.Ordinal);
        Assert.Contains("igloo.seeddiskguid", kickstart, StringComparison.Ordinal);
        Assert.Contains("selected target is the installer seed disk", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("blkid -L OEMDRV", kickstart, StringComparison.Ordinal);
        Assert.Contains("systemctl enable --force plasmalogin.service", kickstart, StringComparison.Ordinal);
        Assert.Contains("systemctl set-default graphical.target", kickstart, StringComparison.Ordinal);
        Assert.Contains("touch \"$SYSIMAGE/etc/plasma-setup-done\"", kickstart, StringComparison.Ordinal);
        Assert.Contains("$OEMDRV/igloo-seed-identity.json", kickstart, StringComparison.Ordinal);
        Assert.Contains("$SYSIMAGE/var/lib/igloo/seed-identity.json", kickstart, StringComparison.Ordinal);
        Assert.Contains("$OEMDRV/ks.cfg", kickstart, StringComparison.Ordinal);
        Assert.Contains("browser-profiles/AppData/Roaming/Mozilla/Firefox|.mozilla/firefox", kickstart, StringComparison.Ordinal);
        Assert.Contains("Google Chrome|browser-profiles/AppData/Local/Google/Chrome/User Data", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData/Local/Google/Chrome/User Data|.wrong-gecko-destination", kickstart,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ntfs-3g", kickstart, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FedoraKdePlugin().RenderInstallerConfigAsync(
            manifest with { Browsers = [manifest.Browsers[0] with { ProfileStagingPath = "" }] }));
    }

    [Fact]
    public async Task FedoraConfigBindsStagedBrowserProfilesToSelectedSources()
    {
        var selected = new BrowserMigration
        {
            Name = "Mozilla Firefox", Engine = "gecko",
            SourceRelativePath = "AppData/Roaming/Mozilla/Firefox",
            DestRelativePath = ".mozilla/firefox",
            ProfileStagingPath = "browser-profiles/AppData/Roaming/Mozilla/Firefox",
        };
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024, TargetDiskGuid = TargetGuid },
            Browsers = [selected],
        };
        var config = await new FedoraKdePlugin().RenderInstallerConfigAsync(manifest);
        var kickstart = Encoding.UTF8.GetString(config.Contents.Span);
        Assert.Contains("browser-profiles/AppData/Roaming/Mozilla/Firefox|.mozilla/firefox", kickstart,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{{STAGED_BROWSER_MAP}}", kickstart, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FedoraKdePlugin().RenderInstallerConfigAsync(
            manifest with { Browsers = [selected with { ProfileStagingPath = "browser-profiles/other" }] }));
    }

    [Fact]
    public async Task FedoraConfigCopiesOnlySelectedStagedFoldersAndRefusesMissingOnes()
    {
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024, TargetDiskGuid = TargetGuid },
            Files = new FileMigrationPlan
            {
                StagingPath = "fixture",
                IncludedFolders = ["Documents", "Pictures"],
                Folders =
                [
                    new MigrationFolder { Name = "Documents", SourceRelativePath = "OneDrive/Documents" },
                    new MigrationFolder { Name = "Pictures", SourceRelativePath = "Pictures" },
                ],
            },
        };
        var config = await new FedoraKdePlugin().RenderInstallerConfigAsync(manifest);
        var kickstart = Encoding.UTF8.GetString(config.Contents.Span);
        Assert.Contains("done <<'STAGEDFOLDERS'\nDocuments\nPictures\nSTAGEDFOLDERS", kickstart,
            StringComparison.Ordinal);
        Assert.Contains("selected folder is missing from OEMDRV/files", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("for folder_src in \"$OEMDRV/files\"/*/", kickstart, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FedoraKdePlugin().RenderInstallerConfigAsync(
            manifest with { Files = manifest.Files with { IncludedFolders = ["../escape"], Folders = [] } }));
    }

    [Fact]
    public async Task FedoraConfigRejectsWifiLineInjection()
    {
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024, TargetDiskGuid = TargetGuid },
            WifiNetworks = [new WifiNetwork { Ssid = "trusted\nmalicious", Security = "open" }],
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new FedoraKdePlugin().RenderInstallerConfigAsync(manifest));
    }

    [Fact]
    public async Task FedoraConfigCarriesTheSavedWifiNetworkIntoTheKickstart()
    {
        // v0.2-alpha behaviour: the primary WPA network reaches %pre so a netinstall can
        // connect; the first-boot agent redacts the seed afterwards. An invalid
        // selection (null list or entry) is still rejected.
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = TargetGuid },
            WifiNetworks = [new WifiNetwork { Ssid = "private-network", Security = "wpa-psk",
                Psk = "synthetic-secret", IsPrimary = true }],
        };
        var withPsk = await new FedoraKdePlugin().RenderInstallerConfigAsync(manifest);
        Assert.Contains("private-network", Encoding.UTF8.GetString(withPsk.Contents.Span),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FedoraKdePlugin().RenderInstallerConfigAsync(manifest with
            { WifiNetworks = null! }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FedoraKdePlugin().RenderInstallerConfigAsync(manifest with
            { WifiNetworks = [null!] }));

        var open = manifest with
        {
            WifiNetworks = [new WifiNetwork { Ssid = "open-network", Security = "open" }],
        };
        var config = await new FedoraKdePlugin().RenderInstallerConfigAsync(open);
        Assert.Contains("open-network\t", Encoding.UTF8.GetString(config.Contents.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FedoraConfigRejectsInvalidSelectedPackageBeforeUsbPreparation()
    {
        var plugin = new FedoraKdePlugin();
        var manifest = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = TargetGuid },
        };
        foreach (var package in new[]
        {
            new SuggestedPackage { WindowsAppName = "fixture", AutoInstall = true },
            new SuggestedPackage { WindowsAppName = "fixture", AutoInstall = true, FlatpakId = "--installation=system" },
            new SuggestedPackage { WindowsAppName = "fixture", AutoInstall = true, FlatpakId = "com..Chrome" },
            new SuggestedPackage { WindowsAppName = "fixture", AutoInstall = true, NativePackage = "--allowerasing" },
            new SuggestedPackage { WindowsAppName = "fixture", AutoInstall = true, NativePackage = "safe..name" },
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
                manifest with { SuggestedPackages = [package] }));

        var valid = await plugin.RenderInstallerConfigAsync(manifest with
        {
            SuggestedPackages = [new SuggestedPackage { WindowsAppName = "Chrome", AutoInstall = true,
                FlatpakId = "com.google.Chrome" }],
        });
        Assert.NotEmpty(valid.Contents.ToArray());
    }

    [Fact]
    public async Task FedoraConfigRejectsKickstartDirectiveInjectionFromUserFields()
    {
        var plugin = new FedoraKdePlugin();
        var valid = Manifest() with
        {
            Hardware = new() { InstallMode = "replace", TargetDiskBytes = 40L * 1024 * 1024 * 1024,
                TargetDiskGuid = TargetGuid },
        };
        foreach (var user in new[]
        {
            valid.User with { FullName = "Eve\"\nclearpart --all" },
            valid.User with { PreferredLinuxUsername = "eve\nreboot" },
            valid.User with { Locale = "en_US.UTF-8\nreboot" },
            valid.User with { Keymap = "us'\nreboot" },
            valid.User with { Timezone = "UTC\nreboot" },
            valid.User with { LinuxPasswordCrypted = "$6$hash\nreboot" },
            valid.User with { WindowsUsername = "fixture\nreboot" },
        })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(
                valid with { User = user }));
        }
        var internationalName = await plugin.RenderInstallerConfigAsync(valid with
        {
            User = valid.User with { FullName = "Zoë O’Connor" },
        });
        Assert.Contains("--gecos=\"Zoë O’Connor\"", Encoding.UTF8.GetString(internationalName.Contents.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedProviderConfigUsesOnlyOwnedStorageAndNoWifiSecretPath()
    {
        var (binding, inventory) = Reviewed();
        var provider = new FedoraKdePlugin();
        Assert.IsAssignableFrom<IReviewedInstallerStorageProvider>(provider);
        var result = await ((IReviewedInstallerStorageProvider)provider).RenderReviewedInstallerConfigAsync(Manifest(), binding,
            binding.Esp.GenerationId, A(inventory));
        Assert.True(result.Availability == ObservationAvailability.Available, result.Code);
        var text = Encoding.UTF8.GetString(result.Value.Contents.Span);
        Assert.Contains("part / --onpart=/dev/disk/by-partuuid/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("autopart --type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("falling back to largest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nmcli device wifi connect", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-storage.cfg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-network.cfg", text, StringComparison.Ordinal);
        Assert.Contains("python3 /tmp/igloo-validate-seed.py", text, StringComparison.Ordinal);
        Assert.Contains("Reviewed owned root and Linux ESP identities verified", text,
            StringComparison.Ordinal);
        Assert.Contains("verify_owned_partition", text, StringComparison.Ordinal);
        Assert.Contains("already has a filesystem", text, StringComparison.Ordinal);
        Assert.Contains("filesystem changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("echo \"autopart", text, StringComparison.Ordinal);
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
        var changedManifest = await provider.RenderReviewedInstallerConfigAsync(Manifest() with
        {
            Hardware = Manifest().Hardware with { TargetDiskGuid = Guid.NewGuid() },
        }, binding, binding.Esp.GenerationId, A(inventory));
        Assert.Equal("FedoraReviewedTargetManifestChanged", changedManifest.Code);
    }

    [Fact]
    public async Task DeferredReviewedConfigBindsOnlyReceiptedOwnedPartitionsAtInstallerRuntime()
    {
        var (binding, _) = Reviewed();
        var provider = new FedoraKdePlugin();
        var result = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest(),
            binding, binding.Esp.GenerationId);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        var kickstart = Encoding.UTF8.GetString(result.Value.Contents.Span);
        var root = binding.Layout.StorageOwnership!.CreatedPartitions.Single(part =>
            part.Role == PreparationRole.LinuxRoot).Identity;
        Assert.Contains("EXPECTED_PARTITIONS=$(LC_ALL=C sort", kickstart, StringComparison.Ordinal);
        Assert.Contains("Reviewed complete target GPT partition set verified", kickstart,
            StringComparison.Ordinal);
        Assert.Contains("verify_owned_partition", kickstart, StringComparison.Ordinal);
        Assert.Contains($"{root.PartitionGuid:D}|{root.PartitionType:D}|{root.OffsetBytes}|{root.SizeBytes}",
            kickstart, StringComparison.Ordinal);
        Assert.Contains($"printf 'part / --onpart=/dev/disk/by-partuuid/{root.PartitionGuid:D} --fstype=ext4", kickstart,
            StringComparison.Ordinal);
        Assert.Contains("has no matching by-partuuid device path", kickstart,
            StringComparison.Ordinal);
        Assert.Contains("%include /tmp/ks-storage.cfg", kickstart, StringComparison.Ordinal);
        var preStart = kickstart.IndexOf("\n%pre ", StringComparison.Ordinal);
        var preEnd = kickstart.IndexOf("\n%end\n", preStart, StringComparison.Ordinal);
        var storageInclude = kickstart.IndexOf("\n%include /tmp/ks-storage.cfg\n",
            StringComparison.Ordinal);
        Assert.True(preStart >= 0 && preEnd > preStart && storageInclude > preEnd,
            "Anaconda must see the completed %pre before the generated storage include.");
        Assert.DoesNotContain("autopart --type", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("\nclearpart --all\n", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("nmcli device wifi connect", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-network.cfg", kickstart, StringComparison.Ordinal);
        Assert.DoesNotContain("{{TARGET_DISK_GUID}}", kickstart, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferredReviewedFixtureIsTheCurrentSyntheticParserInput()
    {
        var (binding, _) = Reviewed();
        var result = await new FedoraKdePlugin().RenderDeferredReviewedInstallerConfigAsync(
            Manifest(), binding, binding.Esp.GenerationId);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        using var resource = typeof(FedoraOwnedStorageRecipeTests).Assembly
            .GetManifestResourceStream("FedoraDeferredReviewedFixture.ks");
        Assert.NotNull(resource);
        using var copy = new MemoryStream();
        await resource.CopyToAsync(copy);
        Assert.Equal(copy.ToArray(), result.Value.Contents.ToArray());
    }

    [Fact]
    public async Task DeferredReviewedConfigRejectsUnreceiptedOrChangedOwnership()
    {
        var (binding, _) = Reviewed();
        var provider = new FedoraKdePlugin();
        var wrongGeneration = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest(),
            binding, Guid.NewGuid());
        Assert.Equal("FedoraDeferredOwnershipInvalid", wrongGeneration.Code);
        var missingShrink = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest(),
            binding with { Layout = binding.Layout with { ShrinkReceipt = null } },
            binding.Esp.GenerationId);
        Assert.Equal("FedoraDeferredOwnershipInvalid", missingShrink.Code);
        var root = binding.Layout.StorageOwnership!.CreatedPartitions.Single(part =>
            part.Role == PreparationRole.LinuxRoot);
        var withoutRoot = binding with
        {
            Layout = binding.Layout with
            {
                StorageOwnership = binding.Layout.StorageOwnership with
                {
                    CreatedPartitions = binding.Layout.StorageOwnership.CreatedPartitions.Remove(root),
                },
            },
        };
        var missingRoot = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest(),
            withoutRoot, binding.Esp.GenerationId);
        Assert.NotEqual(ObservationAvailability.Available, missingRoot.Availability);
        var unsafeUuid = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest(),
            binding with
            {
                Esp = binding.Esp with
                {
                    LinuxEsp = binding.Esp.LinuxEsp with
                    { FileSystemUuid = "2222-BBBB\nclearpart --all" },
                },
            }, binding.Esp.GenerationId);
        Assert.Equal("FedoraDeferredOwnershipInvalid", unsafeUuid.Code);
        var wifi = await provider.RenderDeferredReviewedInstallerConfigAsync(Manifest() with
        {
            WifiNetworks = [new WifiNetwork { Ssid = "fixture", Psk = "secret" }],
        }, binding, binding.Esp.GenerationId);
        Assert.Equal("SecureNetworkCredentialHandoffUnavailable", wifi.Code);
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
        Hardware = new()
        {
            InstallMode = "dual-boot", TargetDiskGuid = Id(1),
            TargetDiskBytes = 100000L * 1024 * 1024,
        },
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
            $"part /boot/efi --onpart=/dev/disk/by-partuuid/{binding.Esp.LinuxEsp.Volume.PartitionGuid:D} --noformat\n" +
            $"part / --onpart=/dev/disk/by-partuuid/{root.Identity.PartitionGuid:D} --fstype=ext4\n", recipe);
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
        Assert.DoesNotContain("--onpart=/dev/nvme", first, StringComparison.Ordinal);
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
    public void ActualTemplateStorageAllocatorKeepsRuntimeTargetChecksAndOmitsWifi()
    {
        var (binding, inventory) = Reviewed();
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fedora-kde", "kickstart", "ks.cfg.template"));
        var (legacy, legacyInventory) = Create();
        Assert.NotEqual(ObservationAvailability.Available,
            FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(template, legacy,
                legacy.Esp.GenerationId, A(legacyInventory)).Availability);
        var result = FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(template, binding, binding.Esp.GenerationId, A(inventory)).Value;
        Assert.DoesNotContain("echo \"autopart", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback target:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-storage.cfg", result, StringComparison.Ordinal);
        Assert.Contains("python3 /tmp/igloo-validate-seed.py", result, StringComparison.Ordinal);
        Assert.Contains("GUID_MATCHES=$(( GUID_MATCHES + 1 ))", result, StringComparison.Ordinal);
        Assert.Contains("verify_owned_partition", result, StringComparison.Ordinal);
        Assert.Contains("Reviewed owned root and Linux ESP identities verified", result,
            StringComparison.Ordinal);
        Assert.DoesNotContain("nmcli radio wifi on", result, StringComparison.Ordinal);
        Assert.DoesNotContain("%include /tmp/ks-network.cfg", result, StringComparison.Ordinal);
        Assert.Contains("part / --onpart=/dev/disk/by-partuuid/", result, StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Ambiguous,
            FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(template + "\nautopart\n", binding, binding.Esp.GenerationId, A(inventory)).Availability);
        Assert.Equal("FedoraReviewedPreflightTemplateChanged",
            FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(
                template.Replace("TARGET_BYTES=\"{{TARGET_DISK_BYTES}}\"", "TARGET_BYTES=\"0\"",
                    StringComparison.Ordinal), binding, binding.Esp.GenerationId, A(inventory)).Code);
    }
}
