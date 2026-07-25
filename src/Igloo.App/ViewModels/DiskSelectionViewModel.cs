using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the Disk Selection wizard step (between Migration Setup and File Staging).
///
/// The user designates the target disk AND chooses the installation type:
///  • <c>Dual Boot</c> - the Windows partition is shrunk; Linux is installed in the freed space.
///  • <c>Replace</c>   - the entire disk is erased and Linux is installed alone.
///
/// For dual boot, the user picks how much space to allocate to Linux via
/// <see cref="LinuxSizeGb"/>.  The actual partition resize is deferred to the
/// USB-write step where admin rights are already held.
/// </summary>
public sealed partial class DiskSelectionViewModel : ObservableObject
{
    private const long MinDiskBytes = 20L * 1024 * 1024 * 1024; // 20 GB
    private const int MinLinuxGb = 25;                        // Fedora minimum

    //   Observable state                            

    [ObservableProperty]
    private IReadOnlyList<DiskListItem> _diskItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisk))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(CanDualBoot))]
    [NotifyPropertyChangedFor(nameof(ShowPartitionSizer))]
    [NotifyPropertyChangedFor(nameof(IsInstallModeDualBoot))]
    [NotifyPropertyChangedFor(nameof(IsInstallModeReplace))]
    private DiskListItem? _selectedItem;

    // Installation mode - stored as a bool for simple two-way radio-button binding.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallMode))]
    [NotifyPropertyChangedFor(nameof(IsInstallModeReplace))]
    [NotifyPropertyChangedFor(nameof(ShowPartitionSizer))]
    private bool _isInstallModeDualBoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(LinuxSizeBytes))]
    [NotifyPropertyChangedFor(nameof(WindowsKeepsGb))]
    private int _linuxSizeGb = 50;

    //   Derived                                ─

    public DiskInfo? SelectedDisk => SelectedItem?.Disk;
    public bool IsInstallModeReplace => !IsInstallModeDualBoot;
    public DiskInstallMode InstallMode => IsInstallModeDualBoot
                                            ? DiskInstallMode.DualBoot
                                            : DiskInstallMode.ReplaceDisk;

    /// <summary>True when the selected disk can host a dual-boot (has a shrinkable NTFS partition ≥ 25 GiB).</summary>
    public bool CanDualBoot => SelectedItem?.IsSystemDisk == true
                                        && MaxShrinkableGb >= MinLinuxGb;

    /// <summary>Maximum GiB the Windows partition can be shrunk (headroom for Linux).</summary>
    public int MaxShrinkableGb => (int)(SelectedItem?.MaxShrinkableBytes / (1024L * 1024 * 1024) ?? 0);

    /// <summary>Show the space-allocation slider only in dual-boot mode when possible.</summary>
    public bool ShowPartitionSizer => IsInstallModeDualBoot && CanDualBoot;

    public bool CanProceed => SelectedItem is not null
                                        && (!IsInstallModeDualBoot || LinuxSizeGb >= MinLinuxGb);

    //   Display-only helpers for the proportional allocation bar       ─

    /// <summary>The chosen Linux allocation in bytes (same unit as DiskInfo.TotalBytes).</summary>
    public long LinuxSizeBytes => (long)LinuxSizeGb << 30;

    /// <summary>GiB of the disk that remain untouched (Windows + other partitions).</summary>
    public int WindowsKeepsGb => Math.Max(0,
        (int)((SelectedItem?.Disk.TotalBytes ?? 0) >> 30) - LinuxSizeGb);

    //   Commands                                

    [RelayCommand] void SetDualBoot() { IsInstallModeDualBoot = true; }
    [RelayCommand] void SetReplace() { IsInstallModeDualBoot = false; }

    //   API                                  ─

    /// <summary>
    /// Populates the disk list and pre-selects the system disk.
    /// If the system disk has enough shrinkable space, defaults to Dual Boot mode.
    /// </summary>
    public void Prepare(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        DiskItems = report.Disks
            .Where(d => d.TotalBytes >= MinDiskBytes)
            .OrderByDescending(d => d.Partitions.Any(p => p.IsSystem || p.IsBoot))
            .ThenByDescending(d => d.TotalBytes)
            .Select(d => new DiskListItem(d))
            .ToList();

        SelectedItem = DiskItems.FirstOrDefault(d => d.IsSystemDisk)
                    ?? (DiskItems.Count > 0 ? DiskItems[0] : null);

        // Default to dual boot when viable, replace when not.
        IsInstallModeDualBoot = CanDualBoot;

        // Default Linux size: 50 GiB, capped to available shrinkable space.
        LinuxSizeGb = Math.Min(50, MaxShrinkableGb);
        if (LinuxSizeGb < MinLinuxGb)
            LinuxSizeGb = Math.Min(MinLinuxGb, MaxShrinkableGb);
    }

    // Keep CanDualBoot / ShowPartitionSizer in sync when selection changes.
    partial void OnSelectedItemChanged(DiskListItem? value)
    {
        OnPropertyChanged(nameof(CanDualBoot));
        OnPropertyChanged(nameof(MaxShrinkableGb));
        OnPropertyChanged(nameof(ShowPartitionSizer));
        OnPropertyChanged(nameof(WindowsKeepsGb));

        // Re-evaluate default mode for newly selected disk.
        IsInstallModeDualBoot = CanDualBoot;
        LinuxSizeGb = Math.Min(50, MaxShrinkableGb);
        if (LinuxSizeGb < MinLinuxGb)
            LinuxSizeGb = Math.Min(MinLinuxGb, MaxShrinkableGb);
    }
}

/// <summary>
/// Wraps a <see cref="DiskInfo"/> with computed display properties for the disk-picker UI.
/// </summary>
public sealed class DiskListItem
{
    public DiskInfo Disk { get; }

    /// <summary>True when this disk contains a Windows system or boot partition.</summary>
    public bool IsSystemDisk { get; }

    /// <summary>
    /// Maximum bytes that can be freed by shrinking the Windows NTFS partition.
    /// Sourced from <c>MSFT_Partition.GetSupportedSize()</c> gathered at preflight time.
    /// </summary>
    public long MaxShrinkableBytes { get; }

    /// <summary>Human-readable GiB available for Linux (dual boot).</summary>
    public int MaxShrinkableGb => (int)(MaxShrinkableBytes / (1024L * 1024 * 1024));

    public DiskListItem(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        Disk = disk;
        IsSystemDisk = disk.Partitions.Any(p => p.IsSystem || p.IsBoot);
        MaxShrinkableBytes = disk.Partitions.Count > 0
            ? disk.Partitions.Max(p => p.ShrinkableBytes)
            : 0L;
    }
}
