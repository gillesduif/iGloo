using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Abstractions;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the Disk Selection wizard step (between Migration Setup and File Staging).
///
/// Presents the physical disks found by the pre-flight check so the user can designate
/// which disk Fedora will be installed onto.  The selection is forwarded to
/// <see cref="FileStagingViewModel.Prepare"/> and ultimately embedded in the kickstart
/// as the <c>bootloader --boot-drive</c> and <c>clearpart --drives</c> targets.
/// </summary>
public sealed partial class DiskSelectionViewModel : ObservableObject
{
    private const long MinDiskBytes = 20L * 1024 * 1024 * 1024; // 20 GB — Fedora minimum

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<DiskListItem> _diskItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisk))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private DiskListItem? _selectedItem;

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>The <see cref="DiskInfo"/> the user chose; null until a selection is made.</summary>
    public DiskInfo? SelectedDisk => SelectedItem?.Disk;

    /// <summary>Enables "Next" once the user has picked a disk.</summary>
    public bool CanProceed => SelectedItem is not null;

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Populates the disk list from <paramref name="report"/> and pre-selects the system disk
    /// (the one Windows is installed on) as the default migration target.
    /// Disks smaller than 20 GB are hidden — they cannot host a Fedora installation.
    /// </summary>
    public void Prepare(PreflightReport report)
    {
        DiskItems = report.Disks
            .Where(d => d.TotalBytes >= MinDiskBytes)
            .OrderByDescending(d => d.Partitions.Any(p => p.IsSystem || p.IsBoot)) // system disk first
            .ThenByDescending(d => d.TotalBytes)
            .Select(d => new DiskListItem(d))
            .ToList();

        // Default: the disk that contains the Windows system/boot partition.
        SelectedItem = DiskItems.FirstOrDefault(d => d.IsSystemDisk)
                    ?? DiskItems.FirstOrDefault();
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

    /// <summary>Non-null warning text shown beneath the disk entry when it is the Windows disk.</summary>
    public string? SystemDiskWarning { get; }

    public DiskListItem(DiskInfo disk)
    {
        Disk         = disk;
        IsSystemDisk = disk.Partitions.Any(p => p.IsSystem || p.IsBoot);
        SystemDiskWarning = IsSystemDisk
            ? "Windows is installed on this disk. Selecting it will erase Windows and replace it with Linux."
            : null;
    }
}
