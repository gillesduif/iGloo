using FluentAssertions;
using System.IO;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class DiskSelectionSpaceTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    private sealed class Preparation : IInstallationPreparation
    {
        public required Func<DiskInfo, FileStagingRequest, CancellationToken,
            Task<DirectInstallSpaceEstimate>> Measure { get; init; }
        public int MeasurementCalls { get; private set; }
        public Observation<bool> PreparationAvailability => Observations.Available(true);

        public Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(string distroId,
            DiskInfo targetDisk, IsoAcquisitionResult verifiedIso, FileStagingRequest selectedFiles,
            CancellationToken cancellationToken = default)
        {
            MeasurementCalls++;
            verifiedIso.Sha256Verified.Should().BeTrue();
            distroId.Should().Be(selectedFiles.DistroId);
            return Measure(targetDisk, selectedFiles, cancellationToken);
        }

        public Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes,
            IsoAcquisitionResult verifiedIso, string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selection must only measure available space.");

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Selection must not change boot registration.");
    }

    private static DiskInfo Disk(string id = "system") =>
        new(id, $"Disk {id}", 500 * GiB, 200 * GiB, "GPT",
            [new PartitionInfo(1, "NTFS", 499 * GiB, "C", IsBoot: true,
                IsSystem: true, ShrinkableBytes: 100 * GiB)])
        { GptDiskGuid = Guid.NewGuid() };

    private static PreflightReport Report(params DiskInfo[] disks) => new()
    {
        IsUefi = true,
        SecureBootEnabled = false,
        TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted,
        Disks = disks,
        GpuVendor = "intel",
        TotalRamBytes = 8 * GiB,
        Findings = [],
    };

    private static DistroManifest Mint() => new()
    {
        Id = "linuxmint-cinnamon",
        DisplayName = "Linux Mint",
        Description = "Fixture",
        Iso = new DistroIsoSpec
        {
            DownloadUrl = new Uri("https://example.invalid/mint.iso"), Sha256 = "",
        },
        SupportedInstallModes = ["dual-boot"],
        MinimumRequirements = new DistroRequirements { MinDiskBytes = 20 * GiB },
    };

    private static IsoAcquisitionResult Iso() =>
        new(@"C:\verified\mint.iso", true, true, 4 * GiB, new string('A', 64));

    private static DiskSelectionViewModel Create(Preparation preparation,
        FileStagingRequest? files = null, params DiskInfo[] disks)
    {
        var vm = new DiskSelectionViewModel(preparation);
        vm.Prepare(Report(disks.Length == 0 ? [Disk()] : disks), Mint(), Iso(),
            files ?? new FileStagingRequest("linuxmint-cinnamon", []));
        return vm;
    }

    private static Preparation WithEstimate(DirectInstallSpaceEstimate estimate) => new()
    {
        Measure = (_, _, _) => Task.FromResult(estimate),
    };

    [Fact]
    public void Reported_capacity_reserves_installer_bytes_before_enabling_next()
    {
        var preparation = WithEstimate(new(28954 * MiB, 4710 * MiB, 0));
        var vm = Create(preparation);

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        preparation.MeasurementCalls.Should().Be(1);
        vm.HasSpaceEstimate.Should().BeTrue();
        vm.MaxProvisionalLinuxGb.Should().Be(23);
        vm.LinuxSliderMaximumGb.Should().Be(23);
        vm.LinuxSizeGb.Should().Be(23);
        vm.CanProceed.Should().BeTrue();
        vm.LinuxSizeGb = 27;
        vm.CanProceed.Should().BeFalse("27 GiB plus 4710 MiB requires the reported 32358 MiB");
    }

    [Fact]
    public void Measurement_pending_keeps_next_disabled()
    {
        var completion = new TaskCompletionSource<DirectInstallSpaceEstimate>();
        var vm = Create(new Preparation { Measure = (_, _, _) => completion.Task });

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        vm.IsCheckingSpace.Should().BeTrue();
        vm.HasSpaceEstimate.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
        completion.SetResult(new(60 * GiB, 5 * GiB, 0));
        vm.IsCheckingSpace.Should().BeFalse();
        vm.HasSpaceEstimate.Should().BeTrue();
        vm.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void Failed_measurement_blocks_next_with_an_action_message()
    {
        var vm = Create(new Preparation
        {
            Measure = (_, _, _) => Task.FromException<DirectInstallSpaceEstimate>(
                new IOException("The selected disk could not be measured.")),
        });

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        vm.IsCheckingSpace.Should().BeFalse();
        vm.HasSpaceEstimate.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
        vm.SpaceErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Canceled_refresh_does_not_reuse_the_previous_estimate()
    {
        var cancelMeasurement = false;
        var vm = Create(new Preparation
        {
            Measure = (_, _, token) => cancelMeasurement
                ? Task.FromCanceled<DirectInstallSpaceEstimate>(token)
                : Task.FromResult(new DirectInstallSpaceEstimate(60 * GiB, 5 * GiB, 0)),
        });
        vm.SelectedItem = Assert.Single(vm.DiskItems);
        vm.CanProceed.Should().BeTrue();
        cancelMeasurement = true;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await vm.RefreshSpaceAsync(preserveSelection: true,
            cancellationToken: cancellation.Token);

        vm.IsCheckingSpace.Should().BeFalse();
        vm.HasSpaceEstimate.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Late_response_for_previous_disk_cannot_replace_selected_disk_capacity()
    {
        var first = new TaskCompletionSource<DirectInstallSpaceEstimate>();
        var second = new TaskCompletionSource<DirectInstallSpaceEstimate>();
        var vm = Create(new Preparation
        {
            Measure = (disk, _, _) => disk.DeviceId == "first" ? first.Task : second.Task,
        }, null, Disk("first"), Disk("second"));
        vm.SelectedItem = vm.DiskItems.Single(item => item.Disk.DeviceId == "first");
        vm.SelectedItem = vm.DiskItems.Single(item => item.Disk.DeviceId == "second");

        second.SetResult(new(30 * GiB, 5 * GiB, 0));
        first.SetResult(new(100 * GiB, 5 * GiB, 0));

        vm.SelectedDisk!.DeviceId.Should().Be("second");
        vm.HasSpaceEstimate.Should().BeTrue();
        vm.MaxProvisionalLinuxGb.Should().Be(25);
        vm.LinuxSizeGb.Should().Be(25);
        vm.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void Migration_staging_reservation_reduces_linux_allowance()
    {
        var files = new FileStagingRequest("linuxmint-cinnamon", [@"C:\Users\fixture\Documents"])
        {
            BrowserRelativePaths = [@"AppData\Local\Microsoft\Edge\User Data"],
        };
        FileStagingRequest? measuredFiles = null;
        var vm = Create(new Preparation
        {
            Measure = (_, request, _) =>
            {
                measuredFiles = request;
                return Task.FromResult(new DirectInstallSpaceEstimate(35 * GiB, 5 * GiB, 7 * GiB));
            },
        }, files);

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        measuredFiles.Should().BeSameAs(files);
        vm.MaxProvisionalLinuxGb.Should().Be(23);
        vm.LinuxSizeGb.Should().Be(23);
        vm.LinuxSizeGb = 24;
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Large_free_space_and_old_shrink_estimate_cannot_override_current_measurement()
    {
        var vm = Create(WithEstimate(new(10 * GiB, 5 * GiB, 0)));

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        vm.SelectedDisk!.FreeBytes.Should().Be(200 * GiB);
        vm.MaxProvisionalLinuxGb.Should().Be(5);
        vm.CanDualBoot.Should().BeFalse();
        vm.NeedsMoreDualBootSpace.Should().BeTrue();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Missing_measurement_service_does_not_accept_preflight_capacity()
    {
        var vm = new DiskSelectionViewModel();
        vm.Prepare(Report(Disk()), Mint(), Iso(),
            new FileStagingRequest("linuxmint-cinnamon", []));

        vm.SelectedItem = Assert.Single(vm.DiskItems);

        vm.HasSpaceEstimate.Should().BeFalse();
        vm.CanDualBoot.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public async Task Revalidation_keeps_a_now_oversized_choice_blocked_for_review()
    {
        var estimate = new DirectInstallSpaceEstimate(60 * GiB, 5 * GiB, 0);
        var vm = Create(new Preparation { Measure = (_, _, _) => Task.FromResult(estimate) });
        vm.SelectedItem = Assert.Single(vm.DiskItems);
        vm.LinuxSizeGb = 40;
        vm.CanProceed.Should().BeTrue();
        estimate = new(30 * GiB, 5 * GiB, 0);

        await vm.RefreshSpaceAsync(preserveSelection: true);

        vm.LinuxSizeGb.Should().Be(40, "the user must review a changed allocation");
        vm.MaxProvisionalLinuxGb.Should().Be(25);
        vm.CanProceed.Should().BeFalse();
    }
}
