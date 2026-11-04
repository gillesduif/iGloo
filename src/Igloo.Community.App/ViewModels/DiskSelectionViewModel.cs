using CommunityToolkit.Mvvm.ComponentModel;
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

    public bool HasSpaceEstimate => _spaceEstimate is not null;
    public bool ShowDiskAction => IsCheckingSpace || SpaceErrorMessage is not null || NoSupportedMode ||
        SelectedItem is not null && _dualBootUnavailableMessage is not null;
    public string DiskActionMessage => IsCheckingSpace
        ? "Checking space for Linux, the installer and selected files…"
        : SpaceErrorMessage ?? _dualBootUnavailableMessage ?? UnsupportedModeAction;
    public string CapacitySummary => _spaceEstimate is { } space
        ? $"Installer and migration space reserved: {(space.InstallerBytes + space.StagingBytes) / (double)GiB:N1} GiB."
        : "Select a disk to check the space needed for this installation.";

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
    [NotifyPropertyChangedFor(nameof(WindowsKeepsGb))]
    private int _linuxSizeGb = 50;

    //   Derived                                ─

    public DiskInfo? SelectedDisk => SelectedItem?.Disk;
    public int MinLinuxGb => _minLinuxGb;
    public DiskInstallMode InstallMode { get; } = DiskInstallMode.DualBoot;
    public string DiskSafetyTitle { get; } = "Back up Windows before resizing it";
    public string DiskSafetyDetail { get; } =
        "Making room for Linux changes the Windows partition. Keep a current backup of the files you want to keep.";
    public bool HasEligibleWindowsDisk => DiskItems.Any(item => item.HasWindowsPartition &&
        string.Equals(item.Disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) &&
        item.Disk.GptDiskGuid is { } id && id != Guid.Empty);
    public string WindowsDiskAction { get; } =
        "iGloo Community keeps Windows installed. No eligible Windows disk was found. " +
        "Run iGloo from the Windows installation you want to keep, then run the system check again.";

    
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

    public string UnsupportedModeAction => SelectedItem?.Disk switch
    {
        null => string.Empty,
        var disk when !string.Equals(disk.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) =>
            "Choose a GPT disk, then continue.",
        { GptDiskGuid: null } =>
            "Choose another disk whose identity Windows can verify, then run the system check again.",
        _ when _dualBootUnavailableMessage is not null => _dualBootUnavailableMessage,
        _ when _supportedInstallModes.Contains("dual-boot") && SelectedItem?.HasWindowsPartition == false =>
            "Select the Windows system disk for dual boot.",
        _ => "iGloo Community always keeps Windows. Choose a distribution that supports installing alongside Windows.",
    };

    public bool NeedsMoreDualBootSpace => IsDualBootCandidate && HasSpaceEstimate &&
        MaxProvisionalLinuxGb < MinLinuxGb;

    
    public int MaxShrinkableGb => (int)Math.Min(int.MaxValue,
        (_spaceEstimate?.ShrinkableBytes ?? 0) / GiB);

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

    
    public bool ShowPartitionSizer => CanDualBoot;

    public bool CanProceed => SelectedItem is not null &&
        !IsCheckingSpace && SpaceErrorMessage is null && CanDualBoot &&
        LinuxSizeGb >= MinLinuxGb && LinuxSizeGb <= MaxProvisionalLinuxGb;

    //   Display-only helpers for the proportional allocation bar       ─

    
    public long LinuxSizeBytes => (long)LinuxSizeGb << 30;

    
    public int WindowsKeepsGb => Math.Max(0,
        (int)((SelectedItem?.Disk.TotalBytes ?? 0) >> 30) - LinuxSizeGb);

    //   API                                  ─

    public void Prepare(PreflightReport report, DistroManifest? distro = null,
        IsoAcquisitionResult? verifiedIso = null, FileStagingRequest? selectedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        CancelSpaceCheck();
        _spaceEstimate = null;
        _verifiedIso = verifiedIso;
        _selectedFiles = selectedFiles;
        _distroId = distro?.Id ?? selectedFiles?.DistroId;
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
                _dualBootUnavailableMessage = availability?.Code switch
                {
                    "FedoraInstallerRootBindingUnavailable" =>
                        "Fedora dual boot is not available in this build. Go back to Linux Distribution and choose Debian to install alongside Windows.",
                    "MintDedicatedEspBindingUnavailable" =>
                        "Linux Mint dual boot is not available in this build. Go back to Linux Distribution and choose Debian to install alongside Windows.",
                    _ => "Installing alongside Windows is unavailable in this build. Update or repair iGloo before continuing.",
                };
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
                LinuxSizeGb = CanDualBoot ? Math.Min(Math.Max(50, MinLinuxGb), MaxProvisionalLinuxGb) : 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (generation != _spaceGeneration) return;
            _spaceEstimate = null;
            SpaceErrorMessage = "Select the disk again to finish checking available space.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Security.SecurityException or OverflowException or System.Management.ManagementException)
        {
            if (generation != _spaceGeneration) return;
            _spaceEstimate = null;
            _logger?.LogError(ex, "Could not measure installation space for the selected disk");
            SpaceErrorMessage = "Go back to Configuration, check that the selected files and downloaded image are accessible, then select the disk again.";
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
            nameof(ShowDiskAction), nameof(DiskActionMessage), nameof(CapacitySummary),
            nameof(MaxShrinkableGb), nameof(MaxProvisionalLinuxGb), nameof(LinuxSliderMinimumGb),
            nameof(LinuxSliderMaximumGb), nameof(ShowPartitionSizer) })
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
        OnPropertyChanged(nameof(MaxShrinkableGb));
        OnPropertyChanged(nameof(MaxProvisionalLinuxGb));
        OnPropertyChanged(nameof(LinuxSliderMinimumGb));
        OnPropertyChanged(nameof(LinuxSliderMaximumGb));
        OnPropertyChanged(nameof(ShowPartitionSizer));
        OnPropertyChanged(nameof(WindowsKeepsGb));

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

    public long MaxShrinkableBytes { get; }

    
    public int MaxShrinkableGb => (int)(MaxShrinkableBytes / (1024L * 1024 * 1024));

    public DiskListItem(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        Disk = disk;
        IsSystemDisk = disk.Partitions.Any(p => p.IsSystem || p.IsBoot);
        HasWindowsPartition = disk.Partitions.Any(p => p.IsBoot &&
            string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase));
        MaxShrinkableBytes = disk.Partitions.Where(p => p.IsBoot &&
                string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ShrinkableBytes).DefaultIfEmpty(0L).Max();
    }
}
