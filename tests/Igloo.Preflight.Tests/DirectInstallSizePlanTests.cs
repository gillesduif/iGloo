using Igloo.Core.Abstractions;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallSizePlanTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Fact]
    public void InstallerImagePathWithShellSyntaxRemainsLiteral()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string path = @"C:\Users\O'Brien\Mint$(throw 'executed').iso";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Write-Output " + DirectInstallService.PowerShellLiteral(path));
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(error);
        Assert.Equal(path, output);
    }

    [Fact]
    public void ExternalInstallerMediaRequiresAnExactHttpsDigestForEachBootFile()
    {
        var kernel = new Uri("https://deb.debian.org/installer/vmlinuz");
        var initrd = new Uri("https://deb.debian.org/installer/initrd.gz");
        var spec = new InstallerBootSpec
        {
            MenuTitle = "Debian", KernelCmdline = "fixture", KernelUrl = kernel, InitrdUrl = initrd,
            KernelSha256 = new string('a', 64), InitrdSha256 = new string('b', 64),
        };
        DirectInstallService.ValidateExternalInstallerMedia(spec);
        Assert.Throws<InvalidDataException>(() =>
            DirectInstallService.ValidateExternalInstallerMedia(spec with { InitrdSha256 = null }));
        Assert.Throws<InvalidDataException>(() =>
            DirectInstallService.ValidateExternalInstallerMedia(spec with
            { KernelUrl = new Uri("http://deb.debian.org/installer/vmlinuz") }));
        Assert.Throws<InvalidDataException>(() =>
            DirectInstallService.ValidateExternalInstallerMedia(spec with
            { InitrdUrl = new Uri("/installer/initrd.gz", UriKind.Relative) }));
    }

    [Fact]
    public void FinalInstallerImageReadRejectsBytesChangedAfterAcquisition()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]));
            DirectInstallService.VerifySelectedIso(path, expected, CancellationToken.None);
            File.WriteAllBytes(path, [1, 2, 3, 5]);
            Assert.Throws<InvalidDataException>(() =>
                DirectInstallService.VerifySelectedIso(path, expected, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SmallIsoStaysOnSeedAndContributesToTheSameShrinkUsedByInstaller()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            3 * GiB, 17 * MiB, 89 * MiB, 821 * MiB);

        Assert.False(sizes.IsoOnOwnPartition);
        Assert.Equal(3 * GiB + 17 * MiB + 89 * MiB + 821 * MiB + 512 * MiB,
            sizes.OemDrvBytes);
        Assert.Equal(0, sizes.IsoPartitionBytes);
        Assert.Equal(50 * GiB + sizes.OemDrvBytes, sizes.TotalShrinkBytes);
    }

    [Fact]
    public void LargeIsoGetsSeparateNtfsPartitionAtFat32SingleFileBoundary()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            4 * GiB, 17 * MiB, 89 * MiB, 821 * MiB);

        Assert.True(sizes.IsoOnOwnPartition);
        Assert.Equal(17 * MiB + 89 * MiB + 821 * MiB + 512 * MiB,
            sizes.OemDrvBytes);
        Assert.Equal(4 * GiB + 256 * MiB, sizes.IsoPartitionBytes);
        Assert.Equal(50 * GiB + sizes.OemDrvBytes + sizes.IsoPartitionBytes,
            sizes.TotalShrinkBytes);
    }

    [Fact]
    public void NonCopyingProviderDoesNotReserveIsoBytes()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture" };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            6 * GiB, 17 * MiB, 89 * MiB, 0);

        Assert.False(sizes.IsoOnOwnPartition);
        Assert.Equal(17 * MiB + 89 * MiB + 512 * MiB, sizes.OemDrvBytes);
        Assert.Equal(0, sizes.IsoPartitionBytes);
    }

    [Fact]
    public void SelectedMigrationFilesAreIncludedInSeedCapacity()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture" };
        var withoutFiles = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            0, 17 * MiB, 89 * MiB, 0);
        var withFiles = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            0, 17 * MiB, 89 * MiB, 0, 2 * GiB);

        Assert.Equal(withoutFiles.OemDrvBytes + 2 * GiB, withFiles.OemDrvBytes);
        Assert.Equal(withoutFiles.TotalShrinkBytes + 2 * GiB, withFiles.TotalShrinkBytes);
    }

    [Fact]
    public void DirectInstallRequiresAndIncludesTheCompleteStagedTree()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-staged-content-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Join(directory, "migration-manifest.json"), "{}");
            Assert.Throws<InvalidDataException>(() => DirectInstallService.InspectStagingFiles(directory));
            Directory.CreateDirectory(Path.Join(directory, "igloo-agent"));
            File.WriteAllText(Path.Join(directory, "igloo-agent", "first-boot.sh"), "#!/bin/sh");
            Directory.CreateDirectory(Path.Join(directory, "files", "Documents"));
            File.WriteAllText(Path.Join(directory, "files", "Documents", "a.txt"), "document");

            var files = DirectInstallService.InspectStagingFiles(directory);

            Assert.Contains(files, file => file.RelativePath == Path.Join("files", "Documents", "a.txt") &&
                file.SizeBytes == 8);
            Assert.Equal(3, files.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnrepresentableSizeFailsBeforeAnyStorageAction()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        Assert.Throws<OverflowException>(() => DirectInstallService.PlanStorageSizes(spec,
            long.MaxValue, 4 * GiB, 17 * MiB, 89 * MiB, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectInstallService.PlanStorageSizes(
            spec, -1, 0, 0, 0, 0));
    }

    [Fact]
    public void InjectedInstallerConfigMustExistAndHaveContent()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-initrd-config-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
                ConfigDelivery = ConfigDelivery.InjectIntoInitrd, InitrdConfigPath = "preseed.cfg" };
            Assert.Throws<FileNotFoundException>(() =>
                DirectInstallService.ReadRequiredInitrdConfig(spec, directory));
            File.WriteAllBytes(Path.Join(directory, "preseed.cfg"), []);
            Assert.Throws<InvalidDataException>(() =>
                DirectInstallService.ReadRequiredInitrdConfig(spec, directory));
            File.WriteAllText(Path.Join(directory, "preseed.cfg"), "d-i locale string en_US");
            var (name, contents) = DirectInstallService.ReadRequiredInitrdConfig(spec, directory);
            Assert.Equal("preseed.cfg", name);
            Assert.NotEmpty(contents);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InjectedInstallerConfigCannotUseParentDirectory()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            ConfigDelivery = ConfigDelivery.InjectIntoInitrd, InitrdConfigPath = "../preseed.cfg" };
        Assert.Throws<InvalidDataException>(() =>
            DirectInstallService.ReadRequiredInitrdConfig(spec, Path.GetTempPath()));
    }

    [Fact]
    public void InjectedHelperFilesUseDeclaredStagingPathsAndUniqueInitrdNames()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-initrd-extras-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Join(directory, "igloo-initrd"));
        try
        {
            File.WriteAllText(Path.Join(directory, "igloo-initrd", "helper.sh"), "#!/bin/sh\nexit 0\n");
            var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
                ConfigDelivery = ConfigDelivery.InjectIntoInitrd, InitrdConfigPath = "preseed.cfg",
                InitrdExtraPaths = ["igloo-initrd/helper.sh"] };
            var only = Assert.Single(DirectInstallService.ReadRequiredInitrdExtras(spec, directory));
            Assert.Equal("helper.sh", only.Name);
            Assert.Equal("#!/bin/sh\nexit 0\n", System.Text.Encoding.UTF8.GetString(only.Contents));
            Assert.Throws<InvalidDataException>(() => DirectInstallService.ReadRequiredInitrdExtras(
                spec with { InitrdExtraPaths = ["igloo-initrd/helper.sh", "helper.sh"] }, directory));
            Assert.Throws<InvalidDataException>(() => DirectInstallService.ReadRequiredInitrdExtras(
                spec with { InitrdExtraPaths = ["../helper.sh"] }, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("auto=true ---", "auto=true igloo.seedpartuuid=")]
    [InlineData("inst.ks=hd:LABEL=OEMDRV:/ks.cfg", "inst.ks=hd:LABEL=OEMDRV:/ks.cfg igloo.seedpartuuid=")]
    public void InstallerBootConfigPinsTheSeedPartitionBeforeStartingTheKernel(
        string kernelArgs, string expectedPrefix)
    {
        var diskGuid = Guid.Parse("27897785-4620-47ee-bd61-39da18873519");
        var partitionGuid = Guid.Parse("a6a183b2-f47c-433a-b170-6be254966e4a");
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = kernelArgs };

        var config = DirectInstallService.BuildGrubConfig(spec, "en_US.UTF-8", "us",
            diskGuid, partitionGuid);

        Assert.Contains(expectedPrefix + partitionGuid.ToString("D"), config,
            StringComparison.Ordinal);
        Assert.Contains("igloo.seeddiskguid=" + diskGuid.ToString("D"), config,
            StringComparison.Ordinal);
        Assert.Contains("set igloo_origin=\"$root\"", config, StringComparison.Ordinal);
        Assert.Contains("search --no-floppy --set=igloo_candidate --file /igloo-seed-identity.json", config,
            StringComparison.Ordinal);
        Assert.Contains("if [ \"$igloo_candidate\" != \"$igloo_origin\" ]; then", config,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--fs-uuid", config, StringComparison.Ordinal);
        Assert.DoesNotContain("insmod probe", config, StringComparison.Ordinal);
        Assert.DoesNotContain("--- igloo.seedpartuuid", config, StringComparison.Ordinal);
        Assert.DoesNotContain("igloo.linuxesppartuuid=", config, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DirectInstallService.BuildGrubConfig(spec,
            "en_US.UTF-8", "us", diskGuid, Guid.Empty));
    }

    [Fact]
    public void ReviewedBootConfigCarriesTheDistinctCreatedLinuxEspIdentity()
    {
        var seedDisk = Guid.NewGuid();
        var seedPart = Guid.NewGuid();
        var linuxEsp = Guid.NewGuid();
        var spec = new InstallerBootSpec
        {
            MenuTitle = "Mint",
            KernelCmdline = "automatic-ubiquity file=/preseed.cfg ---",
        };
        var config = DirectInstallService.BuildGrubConfig(spec, "en_US.UTF-8", "us",
            seedDisk, seedPart, linuxEsp);
        Assert.Contains("igloo.linuxesppartuuid=" + linuxEsp.ToString("D") + " ---",
            config, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DirectInstallService.BuildGrubConfig(spec,
            "en_US.UTF-8", "us", seedDisk, seedPart, Guid.Empty));
        Assert.Throws<InvalidDataException>(() => DirectInstallService.BuildGrubConfig(spec,
            "en_US.UTF-8", "us", seedDisk, seedPart, seedPart));
        Assert.Throws<InvalidDataException>(() => DirectInstallService.BuildGrubConfig(
            spec with { KernelCmdline = "igloo.linuxesppartuuid=unreviewed ---" },
            "en_US.UTF-8", "us", seedDisk, seedPart, linuxEsp));
    }

    [Fact]
    public void InstallerBootConfigBindsFedoraMediaByExactSeedPartition()
    {
        var diskGuid = Guid.Parse("27897785-4620-47ee-bd61-39da18873519");
        var partitionGuid = Guid.Parse("a6a183b2-f47c-433a-b170-6be254966e4a");
        var spec = new InstallerBootSpec
        {
            MenuTitle = "Fedora",
            KernelCmdline = "inst.stage2=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}: " +
                "inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg"
        };

        var config = DirectInstallService.BuildGrubConfig(spec, "en_US.UTF-8", "us",
            diskGuid, partitionGuid);

        Assert.Contains("inst.stage2=hd:/dev/disk/by-partuuid/" + partitionGuid.ToString("D") + ":",
            config, StringComparison.Ordinal);
        Assert.Contains("inst.ks=hd:/dev/disk/by-partuuid/" + partitionGuid.ToString("D") + ":/ks.cfg",
            config, StringComparison.Ordinal);
        Assert.DoesNotContain("{SEED_PARTUUID}", config, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DirectInstallService.BuildGrubConfig(
            spec with { KernelCmdline = "inst.ks=hd:{MISSING}:/ks.cfg" },
            "en_US.UTF-8", "us", diskGuid, partitionGuid));
    }
}
