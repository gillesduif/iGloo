using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Services;
using Igloo.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class MigrationStagingCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterCopyRemovesOnlyTheNewIncompleteGeneration(bool cancelled)
    {
        var source = Path.Join(Path.GetTempPath(), "igloo-stage-source-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging", "debian");
        var previous = Path.Join(parent, Guid.NewGuid().ToString("N"));
        var previousFile = Path.Join(previous, "completed.txt");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Join(source, "selected.txt"), "selected private data");
        await File.WriteAllTextAsync(previousFile, "previous completed generation");
        var staging = new RecordingStaging();
        try
        {
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            await registry.LoadAsync(Path.Join(RepositoryRoot(), "src", "Igloo.Cli", "Igloo.Cli",
                "bin", configuration, "portable", "net10.0-windows10.0.19041.0", "distros"));
            Assert.True(registry.TryGet("debian", out _));
            Exception failure = cancelled ? new OperationCanceledException("after copy") :
                new IOException("after copy");
            var hooks = new MigrationStagingHooks(
                _ => Task.FromException<IReadOnlyList<WifiNetwork>>(failure),
                (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest,
                (manifest, _) => manifest);
            var coordinator = new MigrationStagingCoordinator(staging, registry,
                new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
            var user = new UserSetup
            {
                WindowsUsername = "fixture", LinuxUsername = "fixture",
                LinuxPassword = "fixture-secret", SelectedFolderNames = ["Documents"],
                SelectedFolders = [new MigrationFolder
                {
                    Name = "Documents", SourceRelativePath = "selected"
                }]
            };
            var input = new MigrationStagingInput(new FileStagingRequest("debian", [source])
            { FolderNames = ["Documents"] }, user, new PreflightReport
            {
                IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
                BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "fixture",
                TotalRamBytes = 8_000_000_000, Findings = []
            },
                null, DiskInstallMode.ReplaceDisk, 0);

            var thrown = await Record.ExceptionAsync(() => coordinator.StageAsync(input, null));
            Assert.IsType(failure.GetType(), thrown);
            Assert.NotNull(staging.Result);
            Assert.False(Directory.Exists(staging.Result.StagingDirectory));
            Assert.Equal("previous completed generation", await File.ReadAllTextAsync(previousFile));
        }
        finally
        {
            DeleteTestSource(source);
            FileStagingService.RemoveIncompleteGeneration(previous, "debian");
            // Baseline failure leaves the new, known test generation behind.
            if (staging.Result is { } result && Directory.Exists(result.StagingDirectory))
                FileStagingService.RemoveIncompleteGeneration(result.StagingDirectory, "debian");
        }
    }

    [Fact]
    public async Task FedoraSavedWifiPasswordRejectionRemovesNewStageBeforeManifestOrKickstartIsRetained()
    {
        var parent = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging", "fedora-kde");
        var previous = Path.Join(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Join(previous, "completed.txt"), "previous completed generation");
        var staging = new RecordingStaging();
        try
        {
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            await registry.LoadAsync(Path.Join(RepositoryRoot(), "src", "Igloo.Cli", "Igloo.Cli",
                "bin", configuration, "portable", "net10.0-windows10.0.19041.0", "distros"));
            Assert.True(registry.TryGet("fedora-kde", out _));
            var hooks = new MigrationStagingHooks(
                _ => Task.FromResult<IReadOnlyList<WifiNetwork>>(
                    [new WifiNetwork { Ssid = "synthetic-private", Psk = "synthetic-secret" }]),
                (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest, (manifest, _) => manifest);
            var target = new DiskInfo("virtual", "Disposable target", 40L << 30, 0, "GPT", [])
            { GptDiskGuid = Guid.NewGuid() };
            var coordinator = new MigrationStagingCoordinator(staging, registry,
                new FixedChecker(Report() with { Disks = [target] }),
                NullLogger<MigrationStagingCoordinator>.Instance, hooks);
            var user = new UserSetup
            {
                WindowsUsername = "fixture", LinuxUsername = "fixture",
                LinuxPassword = "fixture-secret"
            };
            var input = new MigrationStagingInput(new FileStagingRequest("fedora-kde", []),
                user, new PreflightReport
                {
                    IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
                    BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "fixture",
                    TotalRamBytes = 8_000_000_000, Findings = []
                }, target, DiskInstallMode.ReplaceDisk, 0);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                coordinator.StageAsync(input, null));
            Assert.Contains("Turn off saved Wi-Fi migration", error.Message,
                StringComparison.Ordinal);
            Assert.NotNull(staging.Result);
            Assert.False(Directory.Exists(staging.Result.StagingDirectory));
            Assert.Equal("previous completed generation",
                await File.ReadAllTextAsync(Path.Join(previous, "completed.txt")));
        }
        finally
        {
            FileStagingService.RemoveIncompleteGeneration(previous, "fedora-kde");
            if (staging.Result is { } result && Directory.Exists(result.StagingDirectory))
                FileStagingService.RemoveIncompleteGeneration(result.StagingDirectory,
                    "fedora-kde");
        }
    }

    [Fact]
    public async Task CleanupFailureReportsTheExactIncompleteGeneration()
    {
        var source = Path.Join(Path.GetTempPath(), "igloo-stage-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "selected.txt"), "selected private data");
        var staging = new RecordingStaging();
        try
        {
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            await registry.LoadAsync(Path.Join(RepositoryRoot(), "src", "Igloo.Cli", "Igloo.Cli",
                "bin", configuration, "portable", "net10.0-windows10.0.19041.0", "distros"));
            Assert.True(registry.TryGet("debian", out _));
            var hooks = new MigrationStagingHooks(_ =>
            {
                var copied = Path.Join(staging.Result!.StagingDirectory, "files", "Documents",
                    "selected.txt");
                File.SetAttributes(copied, FileAttributes.ReadOnly);
                return Task.FromException<IReadOnlyList<WifiNetwork>>(
                    new IOException("configuration failed"));
            }, (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest, (manifest, _) => manifest);
            var coordinator = new MigrationStagingCoordinator(staging, registry,
                new FixedChecker(Report()), NullLogger<MigrationStagingCoordinator>.Instance, hooks);
            var user = new UserSetup
            {
                WindowsUsername = "fixture", LinuxUsername = "fixture",
                LinuxPassword = "fixture-secret", SelectedFolderNames = ["Documents"],
                SelectedFolders = [new MigrationFolder
                {
                    Name = "Documents", SourceRelativePath = "selected"
                }]
            };
            var input = new MigrationStagingInput(new FileStagingRequest("debian", [source])
            { FolderNames = ["Documents"] }, user, new PreflightReport
            {
                IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
                BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "fixture",
                TotalRamBytes = 8_000_000_000, Findings = []
            }, null, DiskInstallMode.ReplaceDisk, 0);

            var orphan = await Assert.ThrowsAsync<IncompleteFileStagingCleanupException>(() =>
                coordinator.StageAsync(input, null));
            Assert.Equal(staging.Result!.StagingDirectory, orphan.StagingDirectory);
            Assert.IsType<IOException>(orphan.OriginalError);
            Assert.True(Directory.Exists(orphan.StagingDirectory));
        }
        finally
        {
            DeleteTestSource(source);
            if (staging.Result is { } result && Directory.Exists(result.StagingDirectory))
            {
                var copied = Path.Join(result.StagingDirectory, "files", "Documents", "selected.txt");
                if (File.Exists(copied)) File.SetAttributes(copied, FileAttributes.Normal);
                FileStagingService.RemoveIncompleteGeneration(result.StagingDirectory, "debian");
            }
        }
    }

    private static void DeleteTestSource(string source)
    {
        var full = Path.GetFullPath(source);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        Assert.Equal(temp, Path.GetDirectoryName(full), ignoreCase: OperatingSystem.IsWindows());
        Assert.StartsWith("igloo-stage-source-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class RecordingStaging : IFileStagingService
    {
        private readonly FileStagingService _inner = new(NullLogger<FileStagingService>.Instance);
        public FileStagingResult? Result { get; private set; }

        public async Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            Result = await _inner.StageAsync(request, progress, ct);
            return Result;
        }
    }
}
