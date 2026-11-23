using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class IsoAcquisitionViewModel : ObservableObject
{
    private readonly IIsoWorkflow _acquisition;
    private readonly ILogger<IsoAcquisitionViewModel> _logger;
    private readonly Dictionary<string, Guid?> _unresolvedOperations =
        new(StringComparer.OrdinalIgnoreCase);
    private long _reviewGeneration;
    private int _activeAcquisitions;

    //   Observable state                           

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
    [ObservableProperty] private string _errorTitle = "Download failed";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private IsoAcquisitionResult? _result;

    // Set when the user canceled: the wizard then returns to the previous page without a message.
    [ObservableProperty] private bool _wasCanceled;

    [ObservableProperty] private bool _showLogs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    private bool _requiresReinstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    private bool _canRetryHere = true;

    private bool _historyUnreadable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanCheckStatus))]
    [NotifyPropertyChangedFor(nameof(RetryLabel))]
    private bool _retryBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckStatus))]
    private Guid? _operationToReview;

    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckStatus))]
    private DistroManifest? _distro;

    //   Derived                                

    public double ProgressPercent =>
        BytesTotal is > 0 ? BytesCompleted * 100.0 / BytesTotal.Value : 0;

    public bool IsProgressIndeterminate =>
        BytesTotal is null or 0;

    public bool CanRetry => !RetryBlocked;

    public bool CanCheckStatus => RetryBlocked && Distro is not null &&
        _acquisition is IIsoOperationStatusWorkflow;

    public bool ShowRetry => CanRetryHere && !RequiresReinstall;

    // An unknown end of an earlier download is looked up again; every other error runs the download again.
    public string RetryLabel => RetryBlocked && !_historyUnreadable ? "Refresh" : "Retry";

    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Connecting…",
        IsoAcquisitionPhase.Downloading => "Downloading…",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying…",
        IsoAcquisitionPhase.VerifyingGpg => "Verifying…",
        IsoAcquisitionPhase.Complete => "Complete",
        _ => string.Empty,
    };

    private void ShowError(string title, string message, bool retry = true, bool logs = false, bool reinstall = false)
    {
        ErrorTitle = title;
        ErrorMessage = message;
        CanRetryHere = retry;
        ShowLogs = logs;
        RequiresReinstall = reinstall;
        HasError = true;
    }

    //   Constructor                              

    public IsoAcquisitionViewModel(
        IIsoWorkflow acquisition,
        ILogger<IsoAcquisitionViewModel> logger)
    {
        _acquisition = acquisition;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   

    public void Prepare(DistroManifest distro)
    {
        ArgumentNullException.ThrowIfNull(distro);

        _reviewGeneration++;
        Distro = distro;

        IsComplete = false;
        HasError = false;
        WasCanceled = false;
        ErrorTitle = "Download failed";
        ErrorMessage = null;
        CanRetryHere = true;
        ShowLogs = false;
        RequiresReinstall = false;
        _historyUnreadable = false;
        RetryBlocked = false;
        OperationToReview = null;
        Result = null;
        BytesCompleted = 0;
        BytesTotal = null;
        Phase = IsoAcquisitionPhase.Downloading;
        if (_unresolvedOperations.TryGetValue(distro.Id, out var unresolvedOperation))
            BlockRetryUntilStatusReview(unresolvedOperation);
    }

    //   Command                                

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AcquireAsync(CancellationToken ct)
    {
        if (Distro is not { } distro || RetryBlocked)
            return;

        var generation = _reviewGeneration;

        // Prepare may replace the reviewed distro while an earlier request is
        // still settling. Keep the page busy until every in-flight request ends.
        Interlocked.Increment(ref _activeAcquisitions);
        IsRunning = true;
        HasError = false;
        WasCanceled = false;
        ErrorMessage = null;

        var uiProgress = new Progress<IsoAcquisitionProgress>(p =>
        {
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            Phase = p.Phase;
            BytesCompleted = p.BytesCompleted;
            BytesTotal = p.BytesTotal;
            OnPropertyChanged(nameof(PhaseDisplay));
        });

        // throttle before the UI thread sees it. Phase changes and the final
        // byte always pass through so the bar never sticks below 100%.
        var progress = new Igloo.Core.Services.ThrottledProgress<IsoAcquisitionProgress>(
            uiProgress,
            forceWhen: (cur, prev) => prev is null
                || cur.Phase != prev.Phase
                || (cur.BytesTotal is { } total && cur.BytesCompleted >= total));

        try
        {
            if (_acquisition is IIsoOperationStatusWorkflow reviewer)
            {
                Guid? unresolved;
                try
                {
                    unresolved = await reviewer.FindUnresolvedOperationAsync(distro.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // Any failed history lookup must close the acquisition gate.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "Could not inspect previous ISO operations");
                    if (RememberUnresolvedOperation(distro.Id, null))
                    {
                        _historyUnreadable = true;
                        OnPropertyChanged(nameof(RetryLabel));
                        ShowError("Download could not be started", "Check the logs and try again.", logs: true);
                    }
                    return;
                }
                if (unresolved is { } operationId)
                {
                    RememberUnresolvedOperation(distro.Id, operationId);
                    return;
                }
                if (!IsCurrentAcquisition(distro.Id, generation))
                    return;
            }
            var result = await _acquisition.AcquireAsync(distro, progress, ct);
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            Result = result;
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ISO acquisition cancelled");
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            WasCanceled = true;
        }
        catch (IsoAcquisitionRecoveryRequiredException ex)
        {
            _logger.LogWarning(ex, "ISO operation outcome requires review");
            RememberUnresolvedOperation(distro.Id, ex.OperationId);
        }
        catch (CliTransportException ex) when (ex.Code is
            CliTransportError.ExecutableUnavailable or CliTransportError.IdentityMismatch)
        {
            // CliCommandClient reports these while validating the package,
            // before it launches or sends a request to the CLI process.
            _logger.LogError(ex, "Packaged CLI cannot start an ISO request");
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            ShowError(DamagedTitle, DamagedMessage, retry: false, reinstall: true);
        }
        catch (CliTransportException ex) when (ex.Code == CliTransportError.ProcessStartFailed)
        {
            // Process.Start failed before any request bytes could reach the CLI.
            _logger.LogError(ex, "Packaged CLI could not start an ISO request");
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            ShowError("Download could not be started", "Check the logs and try again.", logs: true);
        }
        catch (CliTransportException ex)
        {
            _logger.LogWarning(ex, "ISO command transport lost an operation outcome");
            RememberUnresolvedOperation(distro.Id, ex.OperationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var commandFailure = ex as IsoAcquisitionCommandException;
            _logger.LogError(ex,
                "ISO acquisition failed for {DistroId}; code {ErrorCode}; operation {OperationId}",
                distro.Id, commandFailure?.Code, commandFailure?.OperationId);
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            ShowCommandFailure(commandFailure?.Code, commandFailure?.DetailCode, distro.DisplayName);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeAcquisitions) == 0)
                IsRunning = false;
        }
    }

    private const string DamagedTitle = "iGloo Community installation is damaged";
    private const string DamagedMessage = "Reinstall iGloo Community and try again.";

    // The drive the download folder is on.
    private static string DownloadDrive => System.IO.Path.GetPathRoot(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData))?.TrimEnd('\\') is { Length: > 0 } root ? root : "C:";

    private void ShowCommandFailure(CommandErrorCode? code, string? detailCode, string distribution)
    {
        switch (code, detailCode)
        {
            case (CommandErrorCode.InternalFailure, "IsoNetworkUnavailable"):
                ShowError("No internet connection", "Check the network connection and try again.");
                break;
            case (CommandErrorCode.InternalFailure, "IsoCacheUnavailable"):
                ShowError("Insufficient disk space", $"Free up space on drive {DownloadDrive} and try again.");
                break;
            case (CommandErrorCode.VerificationFailed, "IsoSourceMetadataUnavailable"):
                ShowError($"Current {distribution} release not found", "Update iGloo Community and try again.", retry: false);
                break;
            case (CommandErrorCode.VerificationFailed, _):
                ShowError("Checksum verification failed",
                    "The downloaded file does not match the published checksum. Try again.");
                break;
            case (CommandErrorCode.ResourceBusy, _):
                ShowError("Another download is in progress", "Wait until it is complete and try again.");
                break;
            case (CommandErrorCode.NotSupported, _):
                ShowError($"{distribution} is not supported", "Select another distribution.", retry: false);
                break;
            case (CommandErrorCode.InvalidRequest or CommandErrorCode.UnsupportedCapability or
                CommandErrorCode.UnsupportedVersion, _):
                ShowError(DamagedTitle, DamagedMessage, retry: false, reinstall: true);
                break;
            default:
                ShowError("Download failed",
                    "Check the network connection and the available disk space and try again.");
                break;
        }
    }

    private bool RememberUnresolvedOperation(string distroId, Guid? operationId)
    {
        // The CLI may finish an earlier request after the user begins reviewing
        // another distro. Keep uncertain history with its original distro.
        // A fresh review of that same distro still needs the recovery gate.
        _unresolvedOperations[distroId] = operationId;
        if (!string.Equals(Distro?.Id, distroId, StringComparison.OrdinalIgnoreCase))
            return false;
        BlockRetryUntilStatusReview(operationId);
        return true;
    }

    private bool IsCurrentAcquisition(string distroId, long generation) =>
        _reviewGeneration == generation &&
        string.Equals(Distro?.Id, distroId, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task CheckStatusAsync(CancellationToken ct)
    {
        if (!RetryBlocked || Distro is not { } distro ||
            _acquisition is not IIsoOperationStatusWorkflow reviewer)
            return;
        var distroId = distro.Id;
        var generation = ++_reviewGeneration;
        try
        {
            var unresolved = await reviewer.FindUnresolvedOperationAsync(distroId, ct);
            if (!IsCurrentReview(distroId, generation))
                return;
            if (unresolved is null)
            {
                // The wizard runs the download again right after this.
                _unresolvedOperations.Remove(distroId);
                OperationToReview = null;
                RetryBlocked = false;
                HasError = false;
            }
            else
            {
                BlockRetryUntilStatusReview(unresolved);
            }
        }
        catch (OperationCanceledException)
        {
            // The block and its message stay as they are.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ISO operation status check failed");
            if (!IsCurrentReview(distroId, generation))
                return;
            ShowLogs = true;
        }
    }

    private void BlockRetryUntilStatusReview(Guid? operationId)
    {
        _reviewGeneration++;
        if (Distro is { } distro)
            _unresolvedOperations[distro.Id] = operationId;
        RetryBlocked = true;
        OperationToReview = operationId is { } id && id != Guid.Empty ? id : null;
        _historyUnreadable = false;
        OnPropertyChanged(nameof(RetryLabel));
        ShowError("Previous download did not complete", "Refresh to check the download status.");
    }

    private bool IsCurrentReview(string distroId, long generation) =>
        string.Equals(Distro?.Id, distroId, StringComparison.OrdinalIgnoreCase) &&
        RetryBlocked && _reviewGeneration == generation;

    //   Property-change hooks                         ─

    partial void OnPhaseChanged(IsoAcquisitionPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));

    partial void OnIsCompleteChanged(bool value) =>
        OnPropertyChanged(nameof(PhaseDisplay));
}
