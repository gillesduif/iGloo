using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class MigrationStagingSelectionTests
{
    [Fact]
    public async Task SelectedTargetMustStillAppearInObservedMachineBeforeCopy()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance);
        var selected = new DiskInfo("stale", "Prior target", 40L << 30, 0, "GPT", [])
        { GptDiskGuid = Guid.NewGuid() };
        var user = new UserSetup
        { WindowsUsername = "fixture", LinuxUsername = "fixture" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report() with { Disks = [selected] }, selected,
                DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(0, staging.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrAmbiguousTargetLayoutStopsBeforeCopy(bool ambiguous)
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var selected = new DiskInfo("selected", "Target", 40L << 30, 0, "GPT",
            [new PartitionInfo(1, "NTFS", 20L << 30, "Data", false, true,
                OffsetBytes: 1L << 20)]) { GptDiskGuid = Guid.NewGuid() };
        var changed = selected with
        {
            Partitions = [selected.Partitions[0] with { SizeBytes = 21L << 30 }]
        };
        var current = ambiguous ? new[] { selected, selected } : [changed];
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report() with { Disks = current }),
            NullLogger<MigrationStagingCoordinator>.Instance);
        var user = new UserSetup
        { WindowsUsername = "fixture", LinuxUsername = "fixture" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report() with { Disks = [selected] }, selected,
                DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(0, staging.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedFolderOrBrowserSelectionIsRejectedBeforeCopy(bool browserMismatch)
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance);
        var selectedBrowser = new BrowserMigration
        {
            Name = "Mozilla Firefox", Engine = "gecko",
            SourceRelativePath = "AppData/Roaming/Mozilla/Firefox",
            DestRelativePath = ".mozilla/firefox"
        };
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedFolderNames = browserMismatch ? [] : ["Pictures"],
            SelectedFolders = browserMismatch ? [] :
                [new MigrationFolder { Name = "Documents", SourceRelativePath = "Documents" }],
            SelectedBrowserNames = browserMismatch ? ["Google Chrome"] : [],
            SelectedBrowsers = browserMismatch ? [selectedBrowser] : []
        };
        var files = new FileStagingRequest("debian", browserMismatch ? [] : ["unused-source"])
        { FolderNames = browserMismatch ? [] : ["Documents"] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(files, user, Report(), null,
                DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(0, staging.Calls);
    }

    [Fact]
    public async Task WhitespaceBrowserSourceIsRejectedBeforeCopy()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var selected = new BrowserMigration
        {
            Name = "Google Chrome", Engine = "chromium", SourceRelativePath = " "
        };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [selected.Name], SelectedBrowsers = [selected]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(0, staging.Calls);
    }

    [Fact]
    public async Task ChangedBrowserDestinationAfterCopyRemovesIncompleteGeneration()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging(createGeneration: true);
        var selected = new BrowserMigration
        {
            Name = "Mozilla Firefox", Engine = "gecko",
            SourceRelativePath = "AppData/Roaming/Mozilla/Firefox",
            DestRelativePath = ".mozilla/firefox"
        };
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult<IReadOnlyList<BrowserMigration>>(
                [browsers[0] with { DestRelativePath = ".unexpected" }]),
            (manifest, _) => manifest, (manifest, _) => manifest);
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [selected.Name], SelectedBrowsers = [selected]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(1, staging.Calls);
        Assert.Equal(selected, Assert.Single(staging.BrowserProfiles!));
        Assert.NotNull(staging.Generation);
        Assert.False(Directory.Exists(staging.Generation));
    }

    [Fact]
    public async Task BrowserWithoutPortableProfileRemainsSelectedWithoutCopyingAnUnresolvedRoot()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging(createGeneration: true);
        var selected = new BrowserMigration
        {
            Name = "Google Chrome", Engine = "chromium", SourceRelativePath = string.Empty,
            IncludesPasswords = true, CredentialsBlob = "stale-synthetic-blob"
        };
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest);
        var selectedTarget = new DiskInfo("virtual", "Virtual target", 40L << 30, 0, "GPT", [])
        { GptDiskGuid = Guid.Parse("16ee3ea7-3453-4f3b-9183-1015d35dcaa0") };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report() with { Disks = [selectedTarget], GpuVendor = "fresh-gpu" }),
            NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture", LinuxPassword = "fixture-secret",
            SelectedBrowserNames = [selected.Name], SelectedBrowsers = [selected]
        };
        try
        {
            var result = await coordinator.StageAsync(new MigrationStagingInput(
                new FileStagingRequest("debian", []), user,
                Report() with { GpuVendor = "stale-gpu" }, selectedTarget,
                DiskInstallMode.DualBoot, 25), null);

            Assert.Empty(staging.BrowserSources!);
            Assert.Empty(staging.BrowserProfiles!);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Join(result.StagingDirectory, "migration-manifest.json")));
            var browsers = manifest.RootElement.GetProperty("browsers");
            Assert.Equal(1, browsers.GetArrayLength());
            var browser = browsers[0];
            Assert.Equal("Google Chrome", browser.GetProperty("name").GetString());
            Assert.Equal(string.Empty, browser.GetProperty("profileStagingPath").GetString());
            Assert.False(browser.GetProperty("includesPasswords").GetBoolean());
            Assert.Equal(JsonValueKind.Null, browser.GetProperty("credentialsBlob").ValueKind);
            Assert.Equal("fresh-gpu", manifest.RootElement.GetProperty("hardware")
                .GetProperty("gpuVendor").GetString());
        }
        finally
        {
            if (staging.Generation is { } generation && Directory.Exists(generation))
                Igloo.Migration.FileStagingService.RemoveIncompleteGeneration(generation, "debian");
        }
    }

    [Fact]
    public async Task SkippingWifiMigrationDoesNotScanOrWriteSavedPasswords()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging(createGeneration: true);
        var scans = 0;
        var hooks = new MigrationStagingHooks(
            _ =>
            {
                scans++;
                return Task.FromResult<IReadOnlyList<WifiNetwork>>(
                    [new WifiNetwork { Ssid = "synthetic", Psk = "synthetic-secret" }]);
            },
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest);
        var selectedTarget = new DiskInfo("virtual", "Virtual target", 40L << 30, 0, "GPT", [])
        { GptDiskGuid = Guid.NewGuid() };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report() with { Disks = [selectedTarget] }),
            NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            LinuxPassword = "fixture-secret",
        };
        try
        {
            var result = await coordinator.StageAsync(new MigrationStagingInput(
                new FileStagingRequest("debian", []), user, Report(),
                selectedTarget, DiskInstallMode.DualBoot, 25)
                { IncludeWifiNetworks = false }, null);
            Assert.Equal(0, scans);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Join(result.StagingDirectory, "migration-manifest.json")));
            Assert.Empty(manifest.RootElement.GetProperty("wifiNetworks").EnumerateArray());
        }
        finally
        {
            if (staging.Generation is { } generation && Directory.Exists(generation))
                Igloo.Migration.FileStagingService.RemoveIncompleteGeneration(generation, "debian");
        }
    }

    private static PreflightReport Report() => new()
    {
        IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "fixture",
        TotalRamBytes = 8_000_000_000, Findings = []
    };

    private sealed class FixedChecker(PreflightReport report) : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(report);
        }
    }

    private static async Task<DistroRegistry> LoadProvidersAsync()
    {
        var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        await registry.LoadAsync(Path.Join(RepositoryRoot(), "src", "Igloo.Cli", "Igloo.Cli",
            "bin", configuration, "portable", "net10.0-windows10.0.19041.0", "distros"));
        Assert.True(registry.TryGet("debian", out _));
        return registry;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class CountingStaging(bool createGeneration = false) : IFileStagingService
    {
        public int Calls { get; private set; }
        public string? Generation { get; private set; }
        public IReadOnlyList<string>? BrowserSources { get; private set; }
        public IReadOnlyList<BrowserMigration>? BrowserProfiles { get; private set; }

        public Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            Calls++;
            BrowserSources = request.BrowserRelativePaths;
            BrowserProfiles = request.BrowserProfiles;
            if (!createGeneration) throw new InvalidOperationException("Staging was reached unexpectedly.");
            Generation = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Igloo", "staging", request.DistroId, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Generation);
            return Task.FromResult(new FileStagingResult(Generation, 0, 0));
        }
    }
}
