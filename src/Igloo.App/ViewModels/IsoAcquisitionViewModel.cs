using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class IsoAcquisitionViewModel : ObservableObject
{
    private readonly IIsoAcquisitionService            _service;
    private readonly ILogger<IsoAcquisitionViewModel>  _logger;
    private IsoSpecification? _spec;

    // ── Observable state ────────────────────────────────────────────────────

    [ObservableProperty]
    private IsoAcquisitionPhase _phase = IsoAcquisitionPhase.Downloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long? _bytesTotal;

    [ObservableProperty] private bool                   _isRunning;
    [ObservableProperty] private bool                   _isComplete;
    [ObservableProperty] private bool                   _hasError;
    [ObservableProperty] private string?                _errorMessage;
    [ObservableProperty] private IsoAcquisitionResult? _result;

    // ── Derived ──────────────────────────────────────────────────────────────

    public double ProgressPercent =>
        BytesTotal is > 0 ? BytesCompleted * 100.0 / BytesTotal.Value : 0;

    public bool IsProgressIndeterminate =>
        BytesTotal is null or 0;

    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Resolving mirror…",
        IsoAcquisitionPhase.Downloading     => "Downloading…",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying SHA-256…",
        IsoAcquisitionPhase.VerifyingGpg    => "Verifying GPG signature…",
        IsoAcquisitionPhase.Complete        => "Complete",
        _                                   => string.Empty,
    };

    // ── Constructor ──────────────────────────────────────────────────────────

    public IsoAcquisitionViewModel(
        IIsoAcquisitionService service,
        ILogger<IsoAcquisitionViewModel> logger)
    {
        _service = service;
        _logger  = logger;
    }

    // ── API called by MainWindowViewModel ────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="IsoSpecification"/> from the chosen distro manifest and
    /// resets all observable state so the page shows a clean start.
    /// Call this before navigating to the acquisition step, then call
    /// <see cref="AcquireCommand"/>.
    /// </summary>
    public void Prepare(DistroManifest distro)
    {
        _spec = new IsoSpecification(
            distro.Id,
            new Uri(distro.Iso.DownloadUrl),
            distro.Iso.Sha256,
            distro.Iso.GpgSignatureUrl is not null ? new Uri(distro.Iso.GpgSignatureUrl) : null,
            distro.Iso.GpgKeyUrl       is not null ? new Uri(distro.Iso.GpgKeyUrl)       : null);

        IsComplete     = false;
        HasError       = false;
        ErrorMessage   = null;
        Result         = null;
        BytesCompleted = 0;
        BytesTotal     = null;
        Phase          = IsoAcquisitionPhase.Downloading;
    }

    // ── Command ──────────────────────────────────────────────────────────────

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AcquireAsync(CancellationToken ct)
    {
        if (_spec is null) return;

        IsRunning    = true;
        HasError     = false;
        ErrorMessage = null;

        var progress = new Progress<IsoAcquisitionProgress>(p =>
        {
            Phase          = p.Phase;
            BytesCompleted = p.BytesCompleted;
            BytesTotal     = p.BytesTotal;
            OnPropertyChanged(nameof(PhaseDisplay));
        });

        try
        {
            Result     = await _service.AcquireAsync(_spec, progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ISO acquisition cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ISO acquisition failed");
            HasError     = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ── Property-change hooks ─────────────────────────────────────────────────

    partial void OnPhaseChanged(IsoAcquisitionPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));

    partial void OnIsCompleteChanged(bool value) =>
        OnPropertyChanged(nameof(PhaseDisplay));
}
