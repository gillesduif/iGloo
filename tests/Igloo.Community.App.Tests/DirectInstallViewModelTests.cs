using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class DirectInstallViewModelTests
{
    private sealed class Preparation : IInstallationPreparation
    {
        public Exception? PreparationFailure { get; set; }
        public int PrepareCalls { get; private set; }
        public int RegisterCalls { get; private set; }
        public Observation<bool> PreparationAvailability => Observations.Available(true);

        public Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes,
            IsoAcquisitionResult verifiedIso, string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return PreparationFailure is null ? Task.CompletedTask :
                Task.FromException(PreparationFailure);
        }

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Maintenance : IWindowsMaintenance
    {
        public int InstallerRestartCalls { get; private set; }
        public Task<int> StartBitLockerDecryptionAsync(string systemDrive,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void RestartToFirmware() { }
        public void RestartToInstaller() => InstallerRestartCalls++;
    }

    private static DirectInstallViewModel Create(Preparation? preparation = null,
        Maintenance? maintenance = null) => new(preparation ?? new Preparation(),
            maintenance ?? new Maintenance(), NullLogger<DirectInstallViewModel>.Instance);

    private static (IsoAcquisitionResult Iso, FileStagingResult Staging, DiskInfo Disk) Inputs() =>
        (new IsoAcquisitionResult(@"C:\verified\debian.iso", true, true, 1024,
                new string('A', 64)),
            new FileStagingResult(@"C:\staged", 0, 0),
            new DiskInfo(@"\\.\PHYSICALDRIVE2", "Selected", 40L << 30, 0, "GPT", [])
                { GptDiskGuid = Guid.NewGuid() });

    [Fact]
    public void Logs_start_expanded_and_collapse_after_preparation()
    {
        var vm = Create();
        vm.LogsExpanded.Should().BeTrue();
        vm.IsComplete = true;
        vm.LogsExpanded.Should().BeFalse();
        vm.LogsExpanded = true;
        vm.LogsExpanded.Should().BeTrue();
    }

    [Fact]
    public async Task Preparing_the_installation_makes_the_reboot_action_available()
    {
        var preparation = new Preparation();
        var vm = Create(preparation);
        var input = Inputs();
        vm.Prepare(input.Iso, input.Staging, input.Disk, 20, "debian");

        await vm.InstallCommand.ExecuteAsync(null);

        preparation.PrepareCalls.Should().Be(1);
        vm.IsComplete.Should().BeTrue();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Reboot_registers_the_installer_boot_before_requesting_restart()
    {
        var preparation = new Preparation();
        var maintenance = new Maintenance();
        var vm = Create(preparation, maintenance);
        var input = Inputs();
        vm.Prepare(input.Iso, input.Staging, input.Disk, 20, "debian");
        await vm.InstallCommand.ExecuteAsync(null);

        await vm.RebootToInstallCommand.ExecuteAsync(null);

        preparation.RegisterCalls.Should().Be(1);
        maintenance.InstallerRestartCalls.Should().Be(1);
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Preparation_failure_does_not_enable_reboot_or_register_boot()
    {
        var preparation = new Preparation
        {
            PreparationFailure = new NotSupportedException("installer preparation unavailable"),
        };
        var maintenance = new Maintenance();
        var vm = Create(preparation, maintenance);
        var input = Inputs();
        vm.Prepare(input.Iso, input.Staging, input.Disk, 20, "debian");
        await vm.InstallCommand.ExecuteAsync(null);
        await vm.RebootToInstallCommand.ExecuteAsync(null);

        vm.IsComplete.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ErrorDetail.Should().Contain("installer preparation unavailable");
        preparation.RegisterCalls.Should().Be(0);
        maintenance.InstallerRestartCalls.Should().Be(0);
    }
}
