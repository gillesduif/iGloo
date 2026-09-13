using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;

namespace Igloo.App.ViewModels;

/// <summary>Explicit selection from a fresh GPT snapshot; never creates or resizes partitions.</summary>
public sealed partial class OwnedTargetSelectionViewModel(IInstallationTargetPreparer preparer) : ObservableObject
{
    private readonly IInstallationTargetPreparer _preparer = preparer
        ?? throw new ArgumentNullException(nameof(preparer));
    private GptDiskLayout? _reviewed;
    private long _generation;
    private long _minimumBytes;

    [ObservableProperty] private IReadOnlyList<InstallationFreeExtent> _freeExtents = [];
    [ObservableProperty] private IReadOnlyList<GptPartitionIdentity> _espChoices = [];
    [ObservableProperty] private string? _error;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private InstallationFreeExtent? _selectedExtent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private GptPartitionIdentity? _selectedEsp;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    [NotifyPropertyChangedFor(nameof(RootSizeGiB))]
    private long _rootLengthBytes;

    public long RootSizeGiB
    {
        get => RootLengthBytes / (1L << 30);
        set => RootLengthBytes = checked(value * (1L << 30));
    }

    public bool CanProceed => !IsLoading && _reviewed is not null
        && SelectedExtent is not null && FreeExtents.Contains(SelectedExtent)
        && SelectedEsp is not null && EspChoices.Contains(SelectedEsp)
        && RootLengthBytes >= _minimumBytes && RootLengthBytes > 0
        && RootLengthBytes % (1024 * 1024) == 0 && RootLengthBytes <= SelectedExtent.LengthBytes;

    public void Clear()
    {
        _generation++;
        _reviewed = null;
        SelectedExtent = null;
        SelectedEsp = null;
        FreeExtents = [];
        EspChoices = [];
        Error = null;
        IsLoading = false;
        OnPropertyChanged(nameof(CanProceed));
    }

    public async Task LoadAsync(DiskInfo disk, long minimumBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytes);
        Clear();
        var generation = _generation;
        _minimumBytes = minimumBytes;
        RootLengthBytes = minimumBytes;
        IsLoading = true;
        try
        {
            if (disk.GptDiskGuid is not Guid guid || guid == Guid.Empty)
                throw new InvalidDataException("The selected disk has no stable GPT identity. Refresh the system check.");
            var layout = await _preparer.ReadLayoutAsync(guid, ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation)
                return;
            InstallationTargetValidation.ValidateLayout(layout);
            if (layout.DiskGuid != guid || layout.DiskSizeBytes != disk.TotalBytes
                || layout.LogicalSectorSize != disk.LogicalSectorSize)
                throw new InvalidDataException("The selected disk changed since discovery. Refresh the system check.");
            _reviewed = layout with { Partitions = layout.Partitions.ToArray() };
            FreeExtents = InstallationTargetAllocation.GetFreeExtents(_reviewed, minimumBytes);
            EspChoices = _reviewed.Partitions.Where(p => p.GptType == InstallationTargetValidation.EfiSystemType)
                .OrderBy(p => p.OffsetBytes).ToArray();
            if (FreeExtents.Count == 0)
                Error = "No existing free extent is large enough. Automatic shrinking is not supported by this path yet.";
            else if (EspChoices.Count == 0)
                Error = "The selected disk has no EFI System Partition.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            if (generation == _generation)
            {
                _reviewed = null;
                Error = ex is OperationCanceledException ? "Disk inspection was cancelled." : ex.Message;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanProceed));
            }
        }
    }

    public InstallationTargetRequest Authorize(Guid installationId)
    {
        if (!CanProceed)
            throw new InvalidOperationException("Select an exact free extent, root size and existing ESP first.");
        return InstallationTargetAllocation.Authorize(installationId, _reviewed!, SelectedExtent!,
            RootLengthBytes, SelectedEsp!.PartitionGuid);
    }
}
