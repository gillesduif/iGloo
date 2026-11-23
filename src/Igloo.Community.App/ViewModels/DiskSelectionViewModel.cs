using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class DiskSelectionViewModel : ObservableObject
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly IInstallationPreparation? _preparation;
    private readonly ILogger<DiskSelectionViewModel>? _logger;
    private IsoAcquisitionResult? _verifiedIso;
    private FileStagingRequest? _selectedFiles;
    private string? _distroId;
    private string? _dualBootUnavailableMessage;
    private DirectInstallSpaceEstimate? _spaceEstimate;
    private CancellationTokenSource? _spaceCancellation;
    private int _spaceGeneration;
    private const int DefaultMinLinuxGb = 25;
    private int _minLinuxGb = DefaultMinLinuxGb;
    private HashSet<string> _supportedInstallModes =
        new HashSet<string>(["dual-boot"], StringComparer.Ordinal);

    public DiskSelectionViewModel(IInstallationPreparation? preparation = null,
        ILogger<DiskSelectionViewModel>? logger = null)
    {
        _preparation = preparation;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isCheckingSpace;

    [ObservableProperty]
    private string? _spaceErrorMessage;

    // The heading that goes with SpaceErrorMessage.
    private string? _spaceErrorTitle;
    private string? _distroName;

    private const string SpaceCheckFailed = "Space check failed";
    private const string DualBootUnavailableTitle = "Dual boot is not available in this version";

    // The drive Windows runs from, as the messages name it.
    private static string SystemDrive => System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') is { Length: > 0 } root ? root : "C:";

    public bool HasSpaceEstimate => _spaceEstimate is not null;
    public bool ShowDiskAction => IsCheckingSpace || SpaceErrorMessage is not null || NoSupportedMode ||
        NeedsMoreDualBootSpace || SelectedItem is not null && _dualBootUnavailableMessage is not null;

    private (string Title, string Message) DiskAction => IsCheckingSpace ? (string.Empty, "Checking available space…")
        : SpaceErrorMessage is not null ? (_spaceErrorTitle ?? SpaceCheckFailed, SpaceErrorMessage)
        : _dualBootUnavailableMessage is not null ? (DualBootUnavailableTitle, _dualBootUnavailableMessage)
        : NeedsMoreDualBootSpace ? ("Insufficient disk space",
            $"Linux requires at least {MinLinuxGb} GB. Free up space on drive {SystemDrive} and try again.")
        : (UnsupportedModeTitle, UnsupportedModeAction);

    public string DiskActionTitle => DiskAction.Title;
    public string DiskActionMessage => DiskAction.Message;
    public bool HasDiskActionTitle => DiskActionTitle.Length > 0;

    // Refresh measures again where a new measurement can change the outcome.
    public bool CanRefreshSpace => !IsCheckingSpace && (SpaceErrorMessage is not null || NeedsMoreDualBootSpace);

    [RelayCommand]
    private Task RefreshSpaceCheckAsync() => RefreshSpaceAsync();

    //   Observable state                            

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEligibleWindowsDisk))]
    private IReadOnlyList<DiskListItem> _diskItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisk))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(CanDualBoot))]
    [NotifyPropertyChangedFor(nameof(NoSupportedMode))]
    [NotifyPropertyChangedFor(nameof(UnsupportedModeAction))]
    [NotifyPropertyChangedFor(nameof(NeedsMoreDualBootSpace))]
    [NotifyPropertyChangedFor(nameof(ShowPartitionSizer))]
    [NotifyPropertyChangedFor(nameof(MaxProvisionalLinuxGb))]
    [NotifyPropertyChangedFor(nameof(LinuxSliderMinimumGb))]
    [NotifyPropertyChangedFor(nameof(LinuxSliderMaximumGb))]
    private DiskListItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _hasChosenInstallMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(LinuxSizeBytes))]
    [NotifyPropertyChangedFor(nameof(UsesUnallocatedSpace))]
    [NotifyPropertyChangedFor(nameof(HasAllocationSummary))]
    [NotifyPropertyChangedFor(nameof(AllocationSummary))]
    private int _linuxSizeGb = 50;

    //   Derived                                ─

    public DiskInfo? SelectedDisk => SelectedItem?.Disk;
    public int MinLinuxGb => _minLinuxGb;
    public DiskInstallMode InstallMode { get; } = DiskInstallMode.DualBoot;

    // The Platform states the outcome of the chosen size; this page only words it.
    private DirectInstallAllocation? Allocation =>
        CanDualBoot && LinuxSizeGb > 0 ? _spaceEstimate?.Allocate(LinuxSizeBytes) : null;

    public bool UsesUnallocatedSpace => Allocation is { ResizesWindows: false };

    // An installer that cannot be confined takes the unallocated space whole: then there is nothing to choose.
    public bool LinuxSizeIsFixed => CanDualBoot && _spaceEstimate is { HonoursLinuxSize: false } space &&
        !space.Allocate((long)MaxProvisionalLinuxGb * GiB).ResizesWindows;

    // Only an installer that is not confined gives Linux another size than the slider shows.
    public bool HasAllocationSummary => UsesUnallocatedSpace && _spaceEstimate is { HonoursLinuxSize: false };

    public string AllocationSummary => HasAllocationSummary && Allocation is { } allocation
        ? $"Linux will use {allocation.LinuxBytes / (double)GiB:N1} GB. {_distroName ?? "This distribution"} uses all unallocated space on the disk."
        : string.Empty;

    public bool HasEligibleWindowsDisk => DiskItems.Any(item => item.HasWindowsPartition &&
        string.Equals(item.Disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) &&
        item.Disk.GptDiskGuid is { } id && id != Guid.Empty);
    public string WindowsDiskTitle { get; } = "No Windows disk detected";
    public string WindowsDiskAction { get; } = "Run iGloo Community from the Windows installation that must be kept.";

    
    private bool IsDualBootCandidate => SelectedItem?.HasWindowsPartition == true
                                        && string.Equals(SelectedItem.Disk.PartitionStyle, "GPT",
                                            StringComparison.OrdinalIgnoreCase)
                                        && SelectedItem.Disk.GptDiskGuid is { } id && id != Guid.Empty
                                        && _dualBootUnavailableMessage is null
                                        && _supportedInstallModes.Contains("dual-boot");

    public bool CanDualBoot => IsDualBootCandidate && HasSpaceEstimate &&
                              MaxProvisionalLinuxGb >= MinLinuxGb;

    // Prefer the specific free-space action over a second generic warning.
    public bool NoSupportedMode => SelectedItem is not null && !CanDualBoot &&
        !NeedsMoreDualBootSpace && !IsCheckingSpace && SpaceErrorMessage is null;

    private (string Title, string Message) UnsupportedMode => SelectedItem?.Disk switch
    {
        null => (string.Empty, string.Empty),
        var disk when !string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) =>
            ("MBR disks are not supported", "Select a GPT disk."),
        { GptDiskGuid: null } =>
            ("Disk could not be identified", "Select another disk."),
        _ when _dualBootUnavailableMessage is not null => (DualBootUnavailableTitle, _dualBootUnavailableMessage),
        _ when _supportedInstallModes.Contains("dual-boot") && SelectedItem?.HasWindowsPartition == false =>
            ("No Windows installation detected on this disk", "Select the disk that contains Windows."),
        _ => ($"{_distroName ?? "This distribution"} does not support dual boot", "Select another distribution."),
    };

    public string UnsupportedModeTitle => UnsupportedMode.Title;
    public string UnsupportedModeAction => UnsupportedMode.Message;

    public bool NeedsMoreDualBootSpace => IsDualBootCandidate && HasSpaceEstimate &&
        MaxProvisionalLinuxGb < MinLinuxGb;

    // Use the same installer footprint as preparation, plus the pending local
    // staging copy. Free filesystem bytes are not proof of shrinkable space.
    public int MaxProvisionalLinuxGb
    {
        get
        {
            if (_spaceEstimate is not { } space) return 0;
            var available = Math.Max(0, space.ShrinkableBytes - space.InstallerBytes);
            available = Math.Max(0, available - space.StagingBytes);
            return (int)Math.Min(int.MaxValue, available / GiB);
        }
    }

    // Keep the hidden WPF RangeBase at 0..0 when no Linux root can be chosen.
    public int LinuxSliderMinimumGb => CanDualBoot ? MinLinuxGb : 0;
    public int LinuxSliderMaximumGb => CanDualBoot ? MaxProvisionalLinuxGb : 0;

    
    public bool ShowPartitionSizer => CanDualBoot && !LinuxSizeIsFixed;

    public bool CanProceed => SelectedItem is not null &&
        !IsCheckingSpace && SpaceErrorMessage is null && CanDualBoot &&
        LinuxSizeGb >= MinLinuxGb && LinuxSizeGb <= MaxProvisionalLinuxGb;

    public long LinuxSizeBytes => (long)LinuxSizeGb << 30;

    //   API                                 ─

    public void Prepare(PreflightReport report, DistroManifest? distro = null,
        IsoAcquisitionResult? verifiedIso = null, FileStagingRequest? selectedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        CancelSpaceCheck();
        _spaceEstimate = null;
        _verifiedIso = verifiedIso;
        _selectedFiles = selectedFiles;
        _distroId = distro?.Id ?? selectedFiles?.DistroId;
        _distroName = distro?.DisplayName;
        SpaceErrorMessage = null;

        _supportedInstallModes = (distro?.SupportedInstallModes ?? ["dual-boot"])
            .ToHashSet(StringComparer.Ordinal);
        _dualBootUnavailableMessage = null;
        if (_supportedInstallModes.Contains("dual-boot"))
        {
            var availability = _distroId is not null
                ? _preparation?.GetDistroPreparationAvailability(_distroId)
                : null;
            if (availability is null || availability.Availability != ObservationAvailability.Available ||
                !availability.Value)
                _dualBootUnavailableMessage = "Update iGloo Community and try again.";
        }
        var providerMinBytes = distro?.MinimumRequirements?.MinDiskBytes;
        _minLinuxGb = providerMinBytes is > 0 and <= (long)int.MaxValue * GiB
            ? Math.Max(20, (int)((providerMinBytes.Value + GiB - 1) / GiB))
            : DefaultMinLinuxGb;
        OnPropertyChanged(nameof(MinLinuxGb));
        OnPropertyChanged(nameof(LinuxSliderMinimumGb));
        OnPropertyChanged(nameof(LinuxSliderMaximumGb));

        DiskItems = report.Disks
            .Where(d => d.TotalBytes >= (long)MinLinuxGb * GiB)
            .OrderByDescending(d => d.Partitions.Any(p => p.IsSystem || p.IsBoot))
            .ThenByDescending(d => d.TotalBytes)
            .Select(d => new DiskListItem(d))
            .ToList();

        SelectedItem = null;
        HasChosenInstallMode = false;
        LinuxSizeGb = 0;
        NotifySpaceChanged();

        // Select the only available card and start its read-only space check.
        if (DiskItems.Count == 1)
            SelectedItem = DiskItems[0];
    }

    // Read-only admission is refreshed on selection and again on Next, before
    // copying user data or invoking any partition operation.
    public async Task RefreshSpaceAsync(bool preserveSelection = false,
        CancellationToken cancellationToken = default)
    {
        CancelSpaceCheck();
        if (!IsDualBootCandidate) return;
        var generation = _spaceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _spaceCancellation = cancellation;
        IsCheckingSpace = true;
        SpaceErrorMessage = null;
        try
        {
            if (_preparation is null || _verifiedIso is null || _selectedFiles is null || _distroId is null)
                throw new InvalidOperationException("The installation inputs are unavailable.");
            var estimate = await _preparation.MeasureSpaceAsync(_distroId, SelectedDisk!,
                _verifiedIso, _selectedFiles, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _spaceGeneration) return;
            if (estimate.ShrinkableBytes < 0 || estimate.InstallerBytes <= 0 || estimate.StagingBytes < 0)
                throw new InvalidOperationException("The installation space measurement is invalid.");
            _spaceEstimate = estimate;
            NotifySpaceChanged(); // Update slider bounds before assigning its default value.
            if (!preserveSelection && !HasChosenInstallMode)
            {
                HasChosenInstallMode = CanDualBoot;
                // With nothing to choose, the request is what the installer takes.
                LinuxSizeGb = !CanDualBoot ? 0
                    : LinuxSizeIsFixed ? MaxProvisionalLinuxGb
                    : Math.Min(Math.Max(50, MinLinuxGb), MaxProvisionalLinuxGb);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation != _spaceGeneration) return;
            _spaceEstimate = null;
            _spaceErrorTitle = "Space check was canceled";
            SpaceErrorMessage = "Try again.";
        }
        catch (InstallationRefusedException ex)
        {
            // A refusal carries its own instruction, such as the file that is too large for the installer partition.
            if (generation != _spaceGeneration) return;
            _spaceEstimate = null;
            _spaceErrorTitle = ex.Heading;
            SpaceErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Security.SecurityException or OverflowException or System.Management.ManagementException)
        {
            if (generation != _spaceGeneration) return;
            _spaceEstimate = null;
            _logger?.LogError(ex, "Could not measure installation space for the selected disk");
            _spaceErrorTitle = SpaceCheckFailed;
            SpaceErrorMessage = "Try again.";
        }
        finally
        {
            if (generation == _spaceGeneration)
            {
                _spaceCancellation = null;
                IsCheckingSpace = false;
                NotifySpaceChanged();
            }
        }
    }

    // Starts the slow read of the installer image while the user is still on Configuration.
    public Task WarmSpaceCheckAsync(string distroId, IsoAcquisitionResult verifiedIso, IReadOnlyList<DiskInfo>? disks = null) =>
        _preparation is null ? Task.CompletedTask : WarmAsync(_preparation, distroId, verifiedIso, disks ?? []);

    private async Task WarmAsync(IInstallationPreparation preparation, string distroId,
        IsoAcquisitionResult verifiedIso, IReadOnlyList<DiskInfo> disks)
    {
        try
        {
            // Off the UI thread, and side by side: the image and the shrink limit are separate reads.
            await Task.Run(() => Task.WhenAll(
                [preparation.WarmSpaceCheckAsync(distroId, verifiedIso),
                 .. disks.Where(disk => disk.Partitions.Any(partition => partition.IsBoot))
                     .Select(disk => preparation.WarmShrinkEstimateAsync(disk))]));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Security.SecurityException or OperationCanceledException)
        {
            // The space check on Target Disk measures the image itself when this did not finish.
            _logger?.LogInformation(ex, "The installer image was not read ahead of the space check");
        }
    }

    public void CancelSpaceCheck()
    {
        _spaceGeneration++;
        _spaceCancellation?.Cancel();
        _spaceCancellation = null;
        IsCheckingSpace = false;
    }

    partial void OnIsCheckingSpaceChanged(bool value) => NotifySpaceChanged();
    partial void OnSpaceErrorMessageChanged(string? value) => NotifySpaceChanged();

    private void NotifySpaceChanged()
    {
        foreach (var property in new[] { nameof(HasSpaceEstimate), nameof(CanProceed),
            nameof(CanDualBoot), nameof(NeedsMoreDualBootSpace), nameof(NoSupportedMode),
            nameof(ShowDiskAction), nameof(DiskActionTitle), nameof(DiskActionMessage), nameof(HasDiskActionTitle),
            nameof(CanRefreshSpace),
            nameof(MaxProvisionalLinuxGb), nameof(LinuxSliderMinimumGb),
            nameof(LinuxSliderMaximumGb), nameof(ShowPartitionSizer), nameof(LinuxSizeIsFixed),
            nameof(UsesUnallocatedSpace), nameof(HasAllocationSummary), nameof(AllocationSummary) })
            OnPropertyChanged(property);
    }

    // Keep CanDualBoot / ShowPartitionSizer in sync when selection changes.
    partial void OnSelectedItemChanged(DiskListItem? value)
    {
        CancelSpaceCheck();
        _spaceEstimate = null;
        SpaceErrorMessage = null;
        OnPropertyChanged(nameof(CanDualBoot));
        OnPropertyChanged(nameof(NoSupportedMode));
        OnPropertyChanged(nameof(UnsupportedModeAction));
        OnPropertyChanged(nameof(NeedsMoreDualBootSpace));
        OnPropertyChanged(nameof(MaxProvisionalLinuxGb));
        OnPropertyChanged(nameof(LinuxSliderMinimumGb));
        OnPropertyChanged(nameof(LinuxSliderMaximumGb));
        OnPropertyChanged(nameof(ShowPartitionSizer));

        // A newly selected disk needs its own allocation measurement.
        HasChosenInstallMode = CanDualBoot;
        LinuxSizeGb = CanDualBoot ? Math.Min(50, MaxProvisionalLinuxGb) : 0;
        NotifySpaceChanged();
        _ = RefreshSpaceAsync();
    }

}

public sealed class DiskListItem
{
    public DiskInfo Disk { get; }

    
    public bool IsSystemDisk { get; }
    public bool HasWindowsPartition { get; }

    public DiskListItem(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        Disk = disk;
        IsSystemDisk = disk.Partitions.Any(p => p.IsSystem || p.IsBoot);
        HasWindowsPartition = disk.Partitions.Any(p => p.IsBoot &&
            string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase));
    }
}
