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

    [Fact]
    public async Task AmbiguousTargetIdentityStopsBeforeCopy()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var selected = new DiskInfo("selected", "Target", 40L << 30, 0, "GPT",
            [new PartitionInfo(1, "NTFS", 20L << 30, "Data", false, true,
                OffsetBytes: 1L << 20)]) { GptDiskGuid = Guid.NewGuid() };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report() with { Disks = [selected, selected] }),
            NullLogger<MigrationStagingCoordinator>.Instance);
        var user = new UserSetup
        { WindowsUsername = "fixture", LinuxUsername = "fixture" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report() with { Disks = [selected] }, selected,
                DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal(0, staging.Calls);
    }

    // Only the disk identity has to hold before the copy; the layout is revalidated
    // right before the NTFS resize (Beta Track B-00b).
    [Fact]
    public async Task ChangedTargetLayoutNoLongerStopsBeforeCopy()
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
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report() with { Disks = [changed] }),
            NullLogger<MigrationStagingCoordinator>.Instance);
        var user = new UserSetup
        { WindowsUsername = "fixture", LinuxUsername = "fixture" };

        var reached = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report() with { Disks = [selected] }, selected,
                DiskInstallMode.ReplaceDisk, 0), null));
        Assert.Equal("Staging was reached unexpectedly.", reached.Message);
        Assert.Equal(1, staging.Calls);
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
    public async Task A_running_selected_browser_stops_staging_before_anything_is_copied()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var edge = new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium",
            SourceRelativePath = "AppData/Local/Microsoft/Edge/User Data"
        };
        var chromeWithoutProfile = new BrowserMigration { Name = "Google Chrome", Engine = "chromium", SourceRelativePath = string.Empty };
        IReadOnlyList<string>? asked = null;
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest)
        {
            FindRunningBrowsers = names => { asked = names; return ["Microsoft Edge"]; },
        };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [edge.Name, chromeWithoutProfile.Name], SelectedBrowsers = [edge, chromeWithoutProfile]
        };

        var refused = await Assert.ThrowsAsync<BrowserProfileStagingException>(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));

        Assert.Equal(BrowserProfileStagingFailure.BrowserRunning, refused.Reason);
        Assert.Equal(["Microsoft Edge"], refused.Browsers);
        Assert.Equal(["Microsoft Edge"], asked);
        Assert.Equal(0, staging.Calls);
    }

    [Fact]
    public async Task Only_a_running_browser_that_holds_a_file_stops_staging()
    {
        var registry = await LoadProvidersAsync();
        var edge = new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium",
            SourceRelativePath = "AppData/Local/Microsoft/Edge/User Data"
        };
        var firefox = new BrowserMigration
        {
            Name = "Mozilla Firefox", Engine = "gecko",
            SourceRelativePath = "AppData/Roaming/Mozilla/Firefox", DestRelativePath = ".mozilla/firefox"
        };
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [edge.Name, firefox.Name], SelectedBrowsers = [edge, firefox]
        };
        foreach (var holding in new[] { new[] { "Mozilla Firefox" }, Array.Empty<string>() })
        {
            var staging = new CountingStaging();
            FileStagingRequest? probed = null;
            var hooks = new MigrationStagingHooks(
                _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
                (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest, (manifest, _) => manifest)
            {
                FindRunningBrowsers = _ => ["Microsoft Edge", "Mozilla Firefox"],
                FindBrowsersHoldingFiles = (request, _) => { probed = request; return holding; },
            };
            var coordinator = new MigrationStagingCoordinator(staging, registry,
                new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);

            var failure = await Record.ExceptionAsync(() => coordinator.StageAsync(
                new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                    Report(), null, DiskInstallMode.ReplaceDisk, 0), null));

            Assert.Equal(["Microsoft Edge", "Mozilla Firefox"], probed!.BrowserProfiles.Select(browser => browser.Name));
            if (holding.Length == 0)
            {
                Assert.Equal(1, staging.Calls);
                continue;
            }
            var refused = Assert.IsType<BrowserProfileStagingException>(failure);
            Assert.Equal(BrowserProfileStagingFailure.BrowserRunning, refused.Reason);
            Assert.Equal(holding, refused.Browsers);
            Assert.Equal(0, staging.Calls);
            // The stop is the last resort: the snapshot was asked for first.
            Assert.Equal(1, staging.RefusedSnapshots);
        }
    }

    [Fact]
    public async Task A_file_a_running_browser_holds_is_read_from_a_snapshot_and_no_browser_is_ended()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging { SnapshotsAvailable = true };
        var edge = new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium",
            SourceRelativePath = "AppData/Local/Microsoft/Edge/User Data"
        };
        var ended = false;
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest)
        {
            FindRunningBrowsers = _ => ["Microsoft Edge"],
            EndWindowlessBrowsers = names => { ended = true; return names; },
        };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [edge.Name], SelectedBrowsers = [edge]
        };

        var failure = await Record.ExceptionAsync(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));

        // The fixture stops where the real service would start copying.
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, staging.Calls);
        Assert.True(staging.CopiedFromSnapshot);
        Assert.False(ended);
    }

    [Fact]
    public async Task A_browser_with_an_open_window_must_be_closed_and_gets_no_snapshot()
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging { SnapshotsAvailable = true };
        var edge = new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium",
            SourceRelativePath = "AppData/Local/Microsoft/Edge/User Data"
        };
        var ended = false;
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest)
        {
            FindRunningBrowsers = _ => ["Microsoft Edge"],
            FindBrowsersWithWindows = names => names,
            EndWindowlessBrowsers = names => { ended = true; return names; },
        };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [edge.Name], SelectedBrowsers = [edge]
        };

        var failure = await Record.ExceptionAsync(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));

        var refused = Assert.IsType<BrowserProfileStagingException>(failure);
        Assert.Equal(BrowserProfileStagingFailure.BrowserRunning, refused.Reason);
        Assert.Equal(["Microsoft Edge"], refused.Browsers);
        Assert.Equal(0, staging.Calls);
        Assert.Equal(0, staging.RefusedSnapshots);
        Assert.False(ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_browser_without_a_window_is_ended_before_the_copy(bool comesBack)
    {
        var registry = await LoadProvidersAsync();
        var staging = new CountingStaging();
        var edge = new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium",
            SourceRelativePath = "AppData/Local/Microsoft/Edge/User Data"
        };
        var lookups = 0;
        IReadOnlyList<string>? asked = null;
        var hooks = new MigrationStagingHooks(
            _ => Task.FromResult<IReadOnlyList<WifiNetwork>>([]),
            (browsers, _, _) => Task.FromResult(browsers),
            (manifest, _) => manifest, (manifest, _) => manifest)
        {
            FindRunningBrowsers = _ => lookups++ == 0 || comesBack ? ["Microsoft Edge"] : [],
            EndWindowlessBrowsers = names => { asked = names; return names; },
        };
        var coordinator = new MigrationStagingCoordinator(staging, registry,
            new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
        var user = new UserSetup
        {
            WindowsUsername = "fixture", LinuxUsername = "fixture",
            SelectedBrowserNames = [edge.Name], SelectedBrowsers = [edge]
        };

        var failure = await Record.ExceptionAsync(() => coordinator.StageAsync(
            new MigrationStagingInput(new FileStagingRequest("debian", []), user,
                Report(), null, DiskInstallMode.ReplaceDisk, 0), null));

        Assert.Equal(["Microsoft Edge"], asked);
        Assert.Equal(2, lookups);
        if (comesBack)
        {
            Assert.Equal(["Microsoft Edge"], Assert.IsType<BrowserProfileStagingException>(failure).Browsers);
            Assert.Equal(0, staging.Calls);
        }
        else
        {
            Assert.Equal(1, staging.Calls);
        }
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

        // Without snapshots the service refuses before it copies, as the real one does.
        public bool SnapshotsAvailable { get; init; }
        public int RefusedSnapshots { get; private set; }
        public bool? CopiedFromSnapshot { get; private set; }

        public Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            if (request.BrowserFilesFromSnapshot && !SnapshotsAvailable)
            {
                RefusedSnapshots++;
                throw new VolumeSnapshotUnavailableException("The fixture has no snapshots.");
            }
            CopiedFromSnapshot = request.BrowserFilesFromSnapshot;
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
