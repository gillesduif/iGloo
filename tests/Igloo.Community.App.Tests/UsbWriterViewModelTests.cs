using System.IO;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class UsbWriterViewModelTests
{
    [Theory]
    [InlineData(DiskInstallMode.ReplaceDisk)]
    [InlineData((DiskInstallMode)99)]
    public void Replacement_or_unknown_mode_is_rejected_before_USB_IO(DiskInstallMode mode)
    {
        var preparation = new RecordingPreparation();
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);

        Assert.Throws<ArgumentOutOfRangeException>(() => view.Prepare(
            new IsoAcquisitionResult("verified.iso", true, true, 1024, new string('a', 64)),
            new FileStagingResult("staging", 0, 0), installMode: mode));

        Assert.Null(preparation.LastInput);
        Assert.Equal(0, preparation.EnumerationCount);
        Assert.False(view.CanWrite);
    }

    [Fact]
    public async Task Drive_refresh_enables_write_only_for_a_current_drive_and_verified_image()
    {
        var preparation = new RecordingPreparation();
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        Assert.False(view.WriteCommand.CanExecute(null));

        preparation.Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
            @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)];
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Null(view.SelectedDrive);
        Assert.False(view.WriteCommand.CanExecute(null));
        view.SelectedDrive = Assert.Single(view.Drives);
        Assert.True(view.WriteCommand.CanExecute(null));
        await view.WriteCommand.ExecuteAsync(null);
        Assert.Equal(verified.VerifiedSha256, preparation.LastInput?.ExpectedIsoSha256);
        Assert.Equal("fedora-kde", preparation.LastInput?.DistroId);
        Assert.Same(verified, preparation.LastInput?.VerifiedIso);
        Assert.Equal(DiskInstallMode.DualBoot, preparation.LastInput?.InstallMode);
        Assert.True(view.IsComplete);
        Assert.False(view.WriteCommand.CanExecute(null));

        var completedDrive = view.SelectedDrive;
        var completedEnumerationCount = preparation.EnumerationCount;
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Equal(completedEnumerationCount, preparation.EnumerationCount);
        Assert.Same(completedDrive, view.SelectedDrive);
        Assert.True(view.IsComplete);

        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target);
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.False(view.WriteCommand.CanExecute(null));

        preparation.Drives = [];
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Null(view.SelectedDrive);
        Assert.False(view.WriteCommand.CanExecute(null));

        preparation.Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
            @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)];
        view.Prepare(verified with { VerifiedSha256 = null },
            new FileStagingResult("staging", 0, 0), target);
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.False(view.WriteCommand.CanExecute(null));

        view.Prepare(verified, new FileStagingResult("staging", 0, 0));
        Assert.False(view.WriteCommand.CanExecute(null));
        view.Prepare(verified, new FileStagingResult("staging", 0, 0),
            target with { GptDiskGuid = null });
        Assert.False(view.WriteCommand.CanExecute(null));
        view.Prepare(verified, new FileStagingResult("staging", 0, 0),
            target with { DeviceId = @"\\.\PHYSICALDRIVE3" });
        Assert.False(view.WriteCommand.CanExecute(null));

        preparation.Drives = [preparation.Drives[0] with { LogicalSectorBytes = 4096 }];
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target);
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);
        Assert.False(view.WriteCommand.CanExecute(null));
        Assert.True(view.HasUnsupportedDrive);
    }

    [Fact]
    public async Task Failed_refresh_clears_stale_drive_and_shows_error()
    {
        var preparation = new RecordingPreparation
        {
            Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)],
        };
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);
        Assert.True(view.WriteCommand.CanExecute(null));

        preparation.EnumerationError = new IOException("device inventory unavailable");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Null(view.SelectedDrive);
        Assert.Empty(view.Drives);
        Assert.False(view.WriteCommand.CanExecute(null));
        Assert.True(view.HasError);
        Assert.Equal("Refresh USB drives", view.ErrorTitle);
        Assert.StartsWith("Select Refresh", view.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("device inventory unavailable", view.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("device inventory unavailable", view.ErrorDetail,
            StringComparison.Ordinal);

        preparation.EnumerationError = null;
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.False(view.HasError);
        Assert.False(view.WriteCommand.CanExecute(null));
        view.SelectedDrive = Assert.Single(view.Drives);
        Assert.True(view.WriteCommand.CanExecute(null));

        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        Assert.Null(view.SelectedDrive);
        Assert.False(view.WriteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_during_write_does_not_replace_the_selected_drive()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new RecordingPreparation
        {
            Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)],
            WriteGate = gate,
        };
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);
        var selected = view.SelectedDrive;

        var writing = view.WriteCommand.ExecuteAsync(null);
        Assert.True(view.IsRunning);
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Equal(1, preparation.EnumerationCount);
        Assert.Same(selected, view.SelectedDrive);
        gate.SetResult();
        await writing;
    }

    [Fact]
    public async Task Cancellation_before_USB_mutation_does_not_claim_a_truncated_image()
    {
        var preparation = new RecordingPreparation
        {
            Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)],
            WriteFailure = new OperationCanceledException(),
        };
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);

        await view.WriteCommand.ExecuteAsync(null);

        Assert.True(view.HasError);
        Assert.StartsWith("Select Refresh", view.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", view.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.IsComplete);
        Assert.False(view.WriteCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_write_leads_with_a_safe_action_and_keeps_the_cause_in_details(
        bool accessDenied)
    {
        const string cause = "USB access failed in the provider";
        var preparation = new RecordingPreparation
        {
            Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)],
            WriteFailure = accessDenied ? new UnauthorizedAccessException(cause) :
                new IOException(cause),
        };
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);

        await view.WriteCommand.ExecuteAsync(null);

        Assert.True(view.HasError);
        Assert.StartsWith("Select Refresh",
            view.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(cause, view.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(cause, view.ErrorDetail, StringComparison.Ordinal);
        Assert.False(view.IsComplete);
        Assert.False(view.WriteCommand.CanExecute(null));

        preparation.WriteFailure = null;
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.Null(view.SelectedDrive);
        Assert.False(view.WriteCommand.CanExecute(null));
        view.SelectedDrive = Assert.Single(view.Drives);
        Assert.True(view.WriteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Fedora_saved_wifi_rejection_guides_restaging_without_exposing_secret_detail()
    {
        var preparation = new RecordingPreparation
        {
            Drives = [new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3", "USBSTOR\\DEVICE-3", 512)],
            WriteFailure = new NotSupportedException(
                "Turn off saved Wi-Fi migration. fixture-sensitive-value"),
        };
        var view = new UsbWriterViewModel(preparation, NullLogger<UsbWriterViewModel>.Instance);
        var verified = new IsoAcquisitionResult("verified.iso", true, true, 1024,
            new string('a', 64));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE0", "Target", 40L << 30, 0,
            "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        view.Prepare(verified, new FileStagingResult("staging", 0, 0), target,
            linuxSizeGb: 20, distroId: "fedora-kde");
        await view.RefreshDrivesCommand.ExecuteAsync(null);
        view.SelectedDrive = Assert.Single(view.Drives);

        await view.WriteCommand.ExecuteAsync(null);

        Assert.True(view.HasError);
        Assert.False(view.IsComplete);
        Assert.Equal("Turn off saved Wi-Fi migration", view.ErrorTitle);
        Assert.StartsWith("Select Back until Configuration", view.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("wired Ethernet", view.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-sensitive-value", view.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Null(view.ErrorDetail);
        Assert.False(view.WriteCommand.CanExecute(null));
    }

    private sealed class RecordingPreparation : IUsbPreparation
    {
        public IReadOnlyList<UsbDriveInfo> Drives { get; set; } = [];
        public Exception? EnumerationError { get; set; }
        public TaskCompletionSource? WriteGate { get; set; }
        public Exception? WriteFailure { get; set; }
        public int EnumerationCount { get; private set; }
        public UsbPreparationInput? LastInput { get; private set; }
        public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken ct = default)
        {
            EnumerationCount++;
            return EnumerationError is { } error
                ? Task.FromException<IReadOnlyList<UsbDriveInfo>>(error)
                : Task.FromResult(Drives);
        }
        public Task WriteAsync(UsbPreparationInput input, IProgress<UsbWriteProgress>? progress,
            IProgress<string>? resizeProgress, CancellationToken cancellationToken = default)
        {
            LastInput = input;
            if (WriteFailure is { } error)
                return Task.FromException(error);
            return WriteGate?.Task ?? Task.CompletedTask;
        }
    }
}
