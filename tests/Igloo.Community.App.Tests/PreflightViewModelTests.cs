using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public class PreflightViewModelTests
{
    private sealed class PreflightStub(Task<PreflightReport> result) : IPreflightWorkflow
    {
        public Func<Task<long>> Reclaim { get; init; } = () => Task.FromResult(0L);

        public Task<PreflightReport> InspectAsync(CancellationToken cancellationToken = default) => result;

        public Task<long> ReclaimAdjacentFreeSpaceAsync(uint diskNumber, CancellationToken cancellationToken = default) =>
            Reclaim();

        public Task RemoveLinuxAsync(IReadOnlyList<LinuxInstallation> installations,
            IReadOnlyList<SeedLeftover> leftovers, bool removingAllLinux,
            IProgress<string>? progress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MaintenanceStub : IWindowsMaintenance
    {
        public Task<int> StartBitLockerDecryptionAsync(string systemDrive,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public void RestartToFirmware() { }
        public void RestartToInstaller() { }
    }

    private static PreflightViewModel Create(Task<PreflightReport> result) => new(
        new PreflightStub(result), new MaintenanceStub(),
        NullLogger<PreflightViewModel>.Instance);

    [Theory]
    [InlineData("Open Windows System Information and check Secure Boot State, then run the system check again.",
        "Secure Boot state could not be detected", "Windows did not return the Secure Boot state. Try again.")]
    [InlineData("Check BIOS Mode in Windows System Information, then run the system check again.",
        "BIOS mode could not be detected", "Windows did not return the BIOS mode. Try again.")]
    public async Task Unverified_firmware_shows_the_required_action_without_a_false_status(
        string failure, string title, string message)
    {
        var vm = Create(Task.FromException<PreflightReport>(new InvalidOperationException(failure)));

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be(title);
        vm.ErrorMessage.Should().Be(message);
        vm.Report.Should().BeNull();
        vm.FirmwareDisplay.Should().Be("Not checked");
        vm.SecureBootDisplay.Should().Be("Not checked");
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public async Task Unexpected_check_failure_shows_a_recovery_action_without_raw_exception_text()
    {
        const string technicalDetail = "WMI 0x80041010 in Win32_ComputerSystem";
        var vm = Create(Task.FromException<PreflightReport>(new InvalidOperationException(technicalDetail)));

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be("System check failed");
        vm.ErrorMessage.Should().Be("An unexpected error occurred. Check the logs and try again.");
        vm.ShowLogs.Should().BeTrue();
        vm.Report.Should().BeNull();
        vm.CanProceed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, "Legacy BIOS", "Not checked", false)]
    [InlineData(true, false, "UEFI", "Disabled", true)]
    [InlineData(true, true, "UEFI", "Enabled", true)]
    public async Task Firmware_cards_display_only_states_observed_in_the_report(
        bool uefi, bool secureBoot, string firmwareDisplay, string secureBootDisplay, bool canProceed)
    {
        var report = new PreflightReport
        {
            IsUefi = uefi,
            SecureBootEnabled = secureBoot,
            TpmPresent = false,
            BitLocker = BitLockerState.NotEncrypted,
            Disks = [],
            GpuVendor = string.Empty,
            TotalRamBytes = 8L << 30,
            Findings = [],
        };
        var vm = Create(Task.FromResult(report));

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.FirmwareDisplay.Should().Be(firmwareDisplay);
        vm.SecureBootDisplay.Should().Be(secureBootDisplay);
        vm.CanProceed.Should().Be(canProceed);
    }

    [Fact]
    public async Task Findings_that_repeat_the_device_card_are_not_listed_and_a_blocker_still_stops_the_wizard()
    {
        var report = new PreflightReport
        {
            IsUefi = true,
            SecureBootEnabled = true,
            TpmPresent = false,
            BitLocker = BitLockerState.NotEncrypted,
            Disks = [],
            GpuVendor = string.Empty,
            TotalRamBytes = 1L << 30,
            Findings =
            [
                new(FindingSeverity.Info, "SECURE_BOOT_ON", "Secure Boot is enabled.", null),
                new(FindingSeverity.Info, "NO_TPM", "No TPM chip detected.", null),
                new(FindingSeverity.Warning, "LOW_RAM", "Only 1024 MB of RAM detected. Most Linux distributions require at least 2 GB.",
                    "Increase RAM to meet the selected distribution's minimum requirement, then rerun the system check."),
            ],
        };
        var vm = Create(Task.FromResult(report));

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.Findings.Should().ContainSingle().Which.Code.Should().Be("LOW_RAM");
        vm.TpmDisplay.Should().Be("Not detected");
        vm.SecureBootDisplay.Should().Be("Enabled");
        vm.CanProceed.Should().BeTrue();

        var blocked = Create(Task.FromResult(report with
        {
            Findings = [new(FindingSeverity.Info, "NO_TPM", "No TPM chip detected.", null),
                new(FindingSeverity.Blocker, "BITLOCKER_UNAVAILABLE", "The system volume's BitLocker state could not be verified.",
                    "Re-run the system check with access to the BitLocker provider before proceeding.")],
        }));

        await blocked.RunCheckCommand.ExecuteAsync(null);

        blocked.Findings.Should().ContainSingle().Which.Code.Should().Be("BITLOCKER_UNAVAILABLE");
        blocked.HasBlockers.Should().BeTrue();
        blocked.CanProceed.Should().BeFalse();

        var quiet = Create(Task.FromResult(report with
        {
            Findings = [new(FindingSeverity.Info, "NO_TPM", "No TPM chip detected.", null)],
        }));

        await quiet.RunCheckCommand.ExecuteAsync(null);

        quiet.HasFindings.Should().BeFalse("the page then shows no results section");
    }

    [Fact]
    public async Task Space_that_Windows_did_not_take_back_is_reported_and_does_not_block_the_check()
    {
        const string notice = "20.0 GB directly behind C: is unallocated: Windows did not extend C: (storage code 42008). " +
            "Check C: for errors with 'chkdsk C: /f' in an administrator terminal, restart Windows and run the system check again.";
        var report = new PreflightReport
        {
            IsUefi = true,
            SecureBootEnabled = true,
            TpmPresent = false,
            BitLocker = BitLockerState.NotEncrypted,
            Disks = [new DiskInfo(@"\.\PHYSICALDRIVE0", "VMware Virtual NVMe Disk", 64L << 30, 20L << 30, "GPT",
                [new PartitionInfo(3, "NTFS", 43L << 30, null, IsSystem: false, IsBoot: true)])],
            GpuVendor = string.Empty,
            TotalRamBytes = 8L << 30,
            Findings = [],
        };
        var refusing = true;
        var vm = new PreflightViewModel(new PreflightStub(Task.FromResult(report))
        {
            Reclaim = () => refusing
                ? Task.FromException<long>(new FreeSpaceNotReclaimedException(notice))
                : Task.FromResult(0L),
        }, new MaintenanceStub(), NullLogger<PreflightViewModel>.Instance);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.LinuxActionStatus.Should().Be(notice);
        vm.HasLinuxActionStatus.Should().BeTrue();
        vm.ErrorMessage.Should().BeNull();
        vm.Report.Should().BeSameAs(report);
        vm.CanProceed.Should().BeTrue();

        refusing = false;
        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.LinuxActionStatus.Should().BeNull();
    }
}
