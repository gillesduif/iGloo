using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;

namespace Igloo.App.ViewModels;

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

    
    public bool CanDualBoot => SelectedItem?.IsSystemDisk == true
                                        && MaxShrinkableGb >= MinLinuxGb;

    
    public int MaxShrinkableGb => (int)(SelectedItem?.MaxShrinkableBytes / (1024L * 1024 * 1024) ?? 0);

    
    public bool ShowPartitionSizer => IsInstallModeDualBoot && CanDualBoot;

    public bool CanProceed => SelectedItem is not null
                                        && (!IsInstallModeDualBoot || LinuxSizeGb >= MinLinuxGb);

    //   Display-only helpers for the proportional allocation bar       ─

    
    public long LinuxSizeBytes => (long)LinuxSizeGb << 30;

    
    public int WindowsKeepsGb => Math.Max(0,
        (int)((SelectedItem?.Disk.TotalBytes ?? 0) >> 30) - LinuxSizeGb);

    //   Commands                                

    [RelayCommand] void SetDualBoot() { IsInstallModeDualBoot = true; }
    [RelayCommand] void SetReplace() { IsInstallModeDualBoot = false; }

    //   API                                  ─

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

public sealed class DiskListItem
{
    public DiskInfo Disk { get; }

    
    public bool IsSystemDisk { get; }

    public long MaxShrinkableBytes { get; }

    
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
