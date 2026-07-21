using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class IsoAcquisitionViewModel : ObservableObject
{
    private readonly IIsoAcquisitionService _service;
    private readonly ILogger<IsoAcquisitionViewModel> _logger;
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

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private IsoAcquisitionResult? _result;

    /// <summary>The distro being acquired — the page shows its logo on the receipt.</summary>
    [ObservableProperty] private DistroManifest? _distro;

    // ── Derived ──────────────────────────────────────────────────────────────

    public double ProgressPercent =>
        BytesTotal is > 0 ? BytesCompleted * 100.0 / BytesTotal.Value : 0;

    public bool IsProgressIndeterminate =>
        BytesTotal is null or 0;

    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Resolving mirror…",
        IsoAcquisitionPhase.Downloading => "Downloading…",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying SHA-256…",
        IsoAcquisitionPhase.VerifyingGpg => "Verifying GPG signature…",
        IsoAcquisitionPhase.Complete => "Complete",
        _ => string.Empty,
    };

    // ── Constructor ──────────────────────────────────────────────────────────

    public IsoAcquisitionViewModel(
        IIsoAcquisitionService service,
        ILogger<IsoAcquisitionViewModel> logger)
    {
        _service = service;
        _logger = logger;
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
        Distro = distro;

        // Load the bundled, trusted signing key (if the distro ships one). Preferred
        // over fetching from a keyserver: the trust anchor ships with the app.
        byte[]? keyData = null;
        if (distro.Iso.GpgKeyFile is { Length: > 0 } keyFile &&
            distro.SourceDirectory is { Length: > 0 } srcDir)
        {
            var keyPath = System.IO.Path.Combine(srcDir, keyFile);
            try
            {
                if (System.IO.File.Exists(keyPath))
                    keyData = System.IO.File.ReadAllBytes(keyPath);
                else
                    _logger.LogWarning("Bundled signing key not found at {Path}", keyPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read bundled signing key {Path}", keyPath);
            }
        }

        _spec = new IsoSpecification(
            distro.Id,
            new Uri(distro.Iso.DownloadUrl),
            distro.Iso.Sha256,
            distro.Iso.GpgSignatureUrl is not null ? new Uri(distro.Iso.GpgSignatureUrl) : null,
            distro.Iso.GpgKeyUrl is not null ? new Uri(distro.Iso.GpgKeyUrl) : null,
            distro.Iso.GpgSignedDataUrl is not null ? new Uri(distro.Iso.GpgSignedDataUrl) : null,
            keyData,
            distro.Iso.GpgKeyFingerprint);

        IsComplete = false;
        HasError = false;
        ErrorMessage = null;
        Result = null;
        BytesCompleted = 0;
        BytesTotal = null;
        Phase = IsoAcquisitionPhase.Downloading;
    }

    // ── Command ──────────────────────────────────────────────────────────────

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AcquireAsync(CancellationToken ct)
    {
        if (_spec is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;

        var uiProgress = new Progress<IsoAcquisitionProgress>(p =>
        {
            Phase = p.Phase;
            BytesCompleted = p.BytesCompleted;
            BytesTotal = p.BytesTotal;
            OnPropertyChanged(nameof(PhaseDisplay));
        });
        // The service reports per 80 KB buffer (hundreds/sec on a fast line);
        // throttle before the UI thread sees it. Phase changes and the final
        // byte always pass through so the bar never sticks below 100%.
        var progress = new Igloo.Core.Services.ThrottledProgress<IsoAcquisitionProgress>(
            uiProgress,
            forceWhen: (cur, prev) => prev is null
                || cur.Phase != prev.Phase
                || (cur.BytesTotal is { } total && cur.BytesCompleted >= total));

        try
        {
            Result = await _service.AcquireAsync(_spec, progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ISO acquisition cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ISO acquisition failed");
            HasError = true;
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
