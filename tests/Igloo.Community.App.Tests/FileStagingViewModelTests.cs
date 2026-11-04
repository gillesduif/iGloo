using System.IO;
using System.Text.Json;
using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class FileStagingViewModelTests
{
    private sealed class FailThenStage(Exception failure) : IMigrationStagingWorkflow
    {
        public int Calls { get; private set; }

        public Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
            IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Calls == 1
                ? Task.FromException<FileStagingResult>(failure)
                : Task.FromResult(new FileStagingResult("C:\\staged", 1, 1));
        }
    }

    private sealed class FailingStage(Exception failure) : IMigrationStagingWorkflow
    {
        public Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
            IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
            => Task.FromException<FileStagingResult>(failure);
    }

    private sealed class CountingStage : IMigrationStagingWorkflow
    {
        public int Calls { get; private set; }
        public Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
            IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FileStagingResult("C:\\staged", 0, 0));
        }
    }

    private sealed class CapturingStage : IMigrationStagingWorkflow
    {
        public LocalMigrationStagingInput? LastInput { get; private set; }

        public Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
            IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
        {
            LastInput = input;
            return Task.FromResult(new FileStagingResult("C:\\staged", 0, 0));
        }
    }

    private sealed class EmptyProfile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => null;
    }

    private sealed class EdgeProfile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => "com.microsoft.Edge";
    }

    [Fact]
    public async Task Default_staging_request_always_preserves_Windows()
    {
        var coordinator = new CapturingStage();
        var vm = Prepared(coordinator);

        await vm.StageCommand.ExecuteAsync(null);

        coordinator.LastInput.Should().NotBeNull();
        coordinator.LastInput!.InstallMode.Should().Be(DiskInstallMode.DualBoot);
    }

    [Theory]
    [InlineData(DiskInstallMode.ReplaceDisk)]
    [InlineData((DiskInstallMode)99)]
    public void Replacement_or_unknown_staging_mode_is_rejected_before_workflow(DiskInstallMode mode)
    {
        var coordinator = new CountingStage();
        var vm = new FileStagingViewModel(coordinator,
            NullLogger<FileStagingViewModel>.Instance, TestStagingRoot());
        var setup = new MigrationSetupViewModel(new EmptyProfile());

        Action prepare = () => PrepareForDistro(vm, setup, "debian", mode);

        prepare.Should().Throw<ArgumentOutOfRangeException>();
        coordinator.Calls.Should().Be(0);
        vm.Result.Should().BeNull();
    }

    [Fact]
    public async Task Cancelled_staging_can_be_retried_to_completion()
    {
        var staging = new FailThenStage(new OperationCanceledException());
        var vm = new FileStagingViewModel(staging,
            NullLogger<FileStagingViewModel>.Instance, TestStagingRoot());
        var setup = new MigrationSetupViewModel(new EmptyProfile());
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "",
            TotalRamBytes = 8L << 30, Findings = [],
        };
        var distro = new DistroManifest
        {
            Id = "debian", DisplayName = "Debian", Description = "Test",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri("https://example.test/debian.iso"),
                Sha256 = new string('A', 64),
            },
        };

        vm.Prepare(setup, report, distro);
        await vm.StageCommand.ExecuteAsync(null);

        vm.IsRunning.Should().BeFalse();
        vm.IsComplete.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Staging failed");
        vm.ErrorMessage.Should().ContainEquivalentOf("go back one step and continue again");

        await vm.StageCommand.ExecuteAsync(null);
        staging.Calls.Should().Be(2);
        vm.HasError.Should().BeFalse();
        vm.IsComplete.Should().BeTrue();
        vm.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task Ordinary_failure_uses_retry_action_and_keeps_the_cause_in_details()
    {
        var vm = Prepared(new FailingStage(new IOException("selected file changed")));

        await vm.StageCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Staging failed");
        vm.ErrorMessage.Should().StartWith("Check the selected files")
            .And.NotContain("selected file changed");
        vm.HasErrorDetail.Should().BeTrue();
        vm.ErrorDetail.Should().Contain("selected file changed");
        vm.HasOrphanDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task Browser_snapshot_failure_names_the_close_and_retry_action()
    {
        var vm = Prepared(new FailingStage(new BrowserProfileStagingException("locked bookmarks")));

        await vm.StageCommand.ExecuteAsync(null);

        vm.IsComplete.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.HasError.Should().BeTrue();
        vm.CanStage.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Close the selected browsers");
        vm.ErrorMessage.Should().Contain("background browser processes").And.Contain("go back one step and continue again");
        vm.ErrorDetail.Should().Contain("locked bookmarks");
    }

    [Fact]
    public async Task Browser_recovery_requires_open_and_close_without_showing_private_paths_then_can_retry()
    {
        const string privatePath = @"C:\Users\PrivateUser\Browser\History";
        var workflow = new FailThenStage(new BrowserProfileStagingException(
            privatePath, new IOException(privatePath), BrowserProfileStagingFailure.RecoveryRequired));
        var vm = Prepared(workflow);

        await vm.StageCommand.ExecuteAsync(null);

        vm.IsComplete.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.HasError.Should().BeTrue();
        vm.CanStage.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Open and close the selected browser");
        vm.ErrorMessage.Should().Contain("Open the browsers selected")
            .And.Contain("then close them").And.Contain("go back one step and continue again")
            .And.NotContain(privatePath);
        vm.ErrorDetail.Should().BeNull();
        vm.HasErrorDetail.Should().BeFalse();

        await vm.StageCommand.ExecuteAsync(null);

        workflow.Calls.Should().Be(2);
        vm.IsComplete.Should().BeTrue();
        vm.HasError.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_browser_lock_offers_explicit_Task_Manager_action_and_retains_retry(bool launchFails)
    {
        var launches = 0;
        var workflow = new FailThenStage(new BrowserProfileStagingException(
            @"private\Network\Cookies", new IOException("private database"),
            BrowserProfileStagingFailure.BrowserInUse));
        var vm = new FileStagingViewModel(workflow, NullLogger<FileStagingViewModel>.Instance,
            TestStagingRoot(), () =>
            {
                launches++;
                if (launchFails) throw new System.ComponentModel.Win32Exception("fixture launch failed");
            });
        var setup = new MigrationSetupViewModel(new EdgeProfile(),
            [new BrowserEntry("Microsoft Edge", @"C:\Fixture\Default")]);
        PrepareForDistro(vm, setup, "debian");
        vm.OpenTaskManagerCommand.CanExecute(null).Should().BeFalse();

        await vm.StageCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be("Browser files are still in use");
        vm.ErrorMessage.Should().Contain("Microsoft Edge").And.Contain("background")
            .And.Contain("Save your browser work").And.Contain("Open Task Manager")
            .And.NotContain("private");
        vm.ErrorDetail.Should().BeNull();
        vm.CanOpenTaskManager.Should().BeTrue();
        vm.CanStage.Should().BeTrue();
        vm.OpenTaskManagerCommand.Execute(null);
        launches.Should().Be(1);
        if (launchFails) vm.ErrorMessage.Should().Contain("Ctrl+Shift+Esc");
        vm.IsComplete.Should().BeFalse();
        setup.GetSelectedBrowserNames().Should().ContainSingle().Which.Should().Be("Microsoft Edge");

        await vm.StageCommand.ExecuteAsync(null);

        vm.IsComplete.Should().BeTrue();
        vm.HasError.Should().BeFalse();
        vm.CanOpenTaskManager.Should().BeFalse();
        vm.BrowserIsInUse.Should().BeFalse();
    }

    [Theory]
    [InlineData("debian", true)]
    [InlineData("linuxmint-cinnamon", true)]
    [InlineData("fedora-kde", true)]
    [InlineData("fedora-workstation", true)]
    public async Task Staging_receives_the_saved_wifi_choice_for_every_distribution(
        string distroId, bool includeWifi)
    {
        var workflow = new CapturingStage();
        var vm = new FileStagingViewModel(workflow,
            NullLogger<FileStagingViewModel>.Instance, TestStagingRoot());
        var setup = new MigrationSetupViewModel(new EmptyProfile());
        setup.ConfigureDistro(distroId);
        PrepareForDistro(vm, setup, distroId);

        await vm.StageCommand.ExecuteAsync(null);

        workflow.LastInput.Should().NotBeNull();
        workflow.LastInput!.IncludeWifiNetworks.Should().Be(includeWifi);
    }

    [Fact]
    public async Task Fedora_saved_wifi_rejection_guides_restaging_without_showing_secret_detail()
    {
        var vm = new FileStagingViewModel(
            new FailingStage(new InvalidDataException(
                "Turn off saved Wi-Fi migration. fixture-sensitive-value")),
            NullLogger<FileStagingViewModel>.Instance, TestStagingRoot());
        var setup = new MigrationSetupViewModel(new EmptyProfile());
        setup.ConfigureDistro("fedora-kde");
        PrepareForDistro(vm, setup, "fedora-kde");

        await vm.StageCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.IsComplete.Should().BeFalse();
        vm.ErrorTitle.Should().Be("Turn off saved Wi-Fi migration");
        vm.ErrorMessage.Should().StartWith("Select Back until Configuration")
            .And.Contain("wired Ethernet").And.NotContain("fixture-sensitive-value");
        vm.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public async Task Cleanup_failure_shows_the_exact_orphan_directory_for_recovery()
    {
        const string orphan = @"C:\Users\Fixture\AppData\Local\Igloo\staging\generation-123";
        var failure = new IncompleteFileStagingCleanupException(orphan,
            new IOException("selected copy failed"),
            new UnauthorizedAccessException("cleanup denied"));
        var vm = Prepared(new FailingStage(failure));

        await vm.StageCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be("Review incomplete migration files");
        vm.ErrorMessage.Should().StartWith("Open the folder below")
            .And.NotContain("cleanup denied");
        vm.HasOrphanDirectory.Should().BeTrue();
        vm.OrphanDirectory.Should().Be(orphan);
        vm.StageCommand.CanExecute(null).Should().BeFalse();
        vm.CanCheckStagingFolder.Should().BeTrue();
        vm.ErrorDetail.Should().Contain(orphan).And.Contain("selected copy failed")
            .And.Contain("cleanup denied");

        PrepareForDebian(vm);
        vm.OrphanDirectory.Should().Be(orphan);
        vm.StageCommand.CanExecute(null).Should().BeFalse();

        vm.CheckStagingFolderCommand.Execute(null);
        vm.StageCommand.CanExecute(null).Should().BeTrue(
            "the read-only scan found no incomplete generation after cleanup");
    }

    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    public async Task Completed_legacy_package_does_not_block_a_new_generation(string distroId)
    {
        var root = TestStagingRoot();
        var legacy = Path.Join(root, distroId);
        Directory.CreateDirectory(Path.Join(legacy, "files"));
        var manifestPath = Path.Join(legacy, "migration-manifest.json");
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            distroId,
            files = new { stagingPath = legacy },
        });
        await File.WriteAllTextAsync(manifestPath, manifest);
        try
        {
            var coordinator = new CountingStage();
            var vm = new FileStagingViewModel(coordinator,
                NullLogger<FileStagingViewModel>.Instance, root);
            var setup = new MigrationSetupViewModel(new EmptyProfile());
            PrepareForDistro(vm, setup, distroId);

            await vm.StageCommand.ExecuteAsync(null);

            coordinator.Calls.Should().Be(1);
            vm.IsComplete.Should().BeTrue();
            vm.RecoveryStateUnknown.Should().BeFalse();
            (await File.ReadAllTextAsync(manifestPath)).Should().Be(manifest);
            Directory.Exists(Path.Join(legacy, "files")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_or_mismatched_legacy_package_requires_review(bool hasWrongIdentity)
    {
        var root = TestStagingRoot();
        var legacy = Path.Join(root, "debian");
        Directory.CreateDirectory(Path.Join(legacy, "files"));
        if (hasWrongIdentity)
            await File.WriteAllTextAsync(Path.Join(legacy, "migration-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    distroId = "fedora-kde",
                    files = new { stagingPath = legacy },
                }));
        try
        {
            var coordinator = new CountingStage();
            var vm = new FileStagingViewModel(coordinator,
                NullLogger<FileStagingViewModel>.Instance, root);
            PrepareForDebian(vm);

            await vm.StageCommand.ExecuteAsync(null);

            coordinator.Calls.Should().Be(0);
            vm.OrphanDirectory.Should().Be(legacy);
            vm.StageCommand.CanExecute(null).Should().BeFalse();
            Directory.Exists(Path.Join(legacy, "files")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_legacy_package_does_not_hide_an_incomplete_new_generation()
    {
        var root = TestStagingRoot();
        var legacy = Path.Join(root, "debian");
        var incomplete = Path.Join(legacy, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(incomplete);
        await File.WriteAllTextAsync(Path.Join(legacy, "migration-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                distroId = "debian",
                files = new { stagingPath = legacy },
            }));
        try
        {
            var coordinator = new CountingStage();
            var vm = Prepared(coordinator, root);

            await vm.StageCommand.ExecuteAsync(null);

            // iGloo's own interrupted generation is removed and staging goes ahead;
            // the completed legacy package beside it is left alone.
            coordinator.Calls.Should().Be(1);
            vm.OrphanDirectory.Should().BeNull();
            Directory.Exists(incomplete).Should().BeFalse();
            File.Exists(Path.Join(legacy, "migration-manifest.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Incomplete_generation_from_an_earlier_process_is_removed_and_staging_continues()
    {
        var root = TestStagingRoot();
        var distro = Path.Join(root, "debian");
        var incomplete = Path.Join(distro, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(incomplete, "files", "Documents"));
        await File.WriteAllTextAsync(Path.Join(incomplete, "files", "Documents", "half-copied.txt"), "interrupted");
        try
        {
            var coordinator = new CountingStage();
            var vm = Prepared(coordinator, root);

            await vm.StageCommand.ExecuteAsync(null);

            // As in v0.2-alpha: a leftover copy iGloo made itself is cleaned up
            // without asking the user to visit AppData.
            Directory.Exists(incomplete).Should().BeFalse();
            vm.HasOrphanDirectory.Should().BeFalse();
            coordinator.Calls.Should().Be(1);
            vm.IsComplete.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(distro)) Directory.Delete(distro);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Unknown_staging_entry_blocks_retry_until_a_read_only_recheck_is_clear()
    {
        var root = TestStagingRoot();
        Directory.CreateDirectory(root);
        var unknown = Path.Join(root, "unexpected.txt");
        await File.WriteAllTextAsync(unknown, "unrecognized staging state");
        try
        {
            var coordinator = new CountingStage();
            var vm = Prepared(coordinator, root);

            await vm.StageCommand.ExecuteAsync(null);

            coordinator.Calls.Should().Be(0);
            vm.RecoveryStateUnknown.Should().BeTrue();
            vm.StageCommand.CanExecute(null).Should().BeFalse();
            File.Delete(unknown);
            vm.CheckStagingFolderCommand.Execute(null);
            vm.StageCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(unknown)) File.Delete(unknown);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static FileStagingViewModel Prepared(IMigrationStagingWorkflow workflow,
        string? stagingRoot = null)
    {
        var vm = new FileStagingViewModel(workflow,
            NullLogger<FileStagingViewModel>.Instance, stagingRoot ?? TestStagingRoot());
        PrepareForDebian(vm);
        return vm;
    }

    private static void PrepareForDebian(FileStagingViewModel vm)
    {
        var setup = new MigrationSetupViewModel(new EmptyProfile());
        PrepareForDistro(vm, setup, "debian");
    }

    private static void PrepareForDistro(FileStagingViewModel vm,
        MigrationSetupViewModel setup, string distroId,
        DiskInstallMode? mode = null)
    {
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "",
            TotalRamBytes = 8L << 30, Findings = [],
        };
        var distro = new DistroManifest
        {
            Id = distroId, DisplayName = distroId, Description = "Test",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri("https://example.test/debian.iso"),
                Sha256 = new string('A', 64),
            },
        };
        if (mode is { } explicitMode)
            vm.Prepare(setup, report, distro, installMode: explicitMode);
        else
            vm.Prepare(setup, report, distro);
    }

    private static string TestStagingRoot() => Path.Join(Path.GetTempPath(),
        "igloo-community-staging-" + Guid.NewGuid().ToString("N"));
}
