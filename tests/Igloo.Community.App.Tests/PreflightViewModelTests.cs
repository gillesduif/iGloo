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
        public Task<PreflightReport> InspectAsync(CancellationToken cancellationToken = default) => result;

        public Task<long> ReclaimAdjacentFreeSpaceAsync(uint diskNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

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
        "Check Secure Boot State in Windows")]
    [InlineData("Check BIOS Mode in Windows System Information, then run the system check again.",
        "Check BIOS Mode in Windows")]
    public async Task Unverified_firmware_shows_the_required_action_without_a_false_status(
        string failure, string title)
    {
        var vm = Create(Task.FromException<PreflightReport>(new InvalidOperationException(failure)));

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be(title);
        vm.ErrorMessage.Should().Be(failure);
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

        vm.ErrorTitle.Should().Be("Run the system check again");
        vm.ErrorMessage.Should().Contain("latest iGloo log").And.NotContain(technicalDetail);
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
}
