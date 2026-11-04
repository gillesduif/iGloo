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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanCheckStatus))]
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

    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Resolving mirror…",
        IsoAcquisitionPhase.Downloading => "Downloading…",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying SHA-256…",
        IsoAcquisitionPhase.VerifyingGpg => "Verifying GPG signature…",
        IsoAcquisitionPhase.Complete => "Complete",
        _ => string.Empty,
    };

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
        ErrorTitle = "Download failed";
        ErrorMessage = null;
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
                        ErrorTitle = "Download failed";
                        ErrorMessage = "Check that the local iGloo CLI can read download history, " +
                                       "then go back one step and continue again.";
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
            ErrorTitle = "Download failed";
            ErrorMessage = "The transfer was cancelled before verification. " +
                           "Retry to resume and verify the installation image.";
            HasError = true;
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
            HasError = true;
            ErrorTitle = "Repair local iGloo package";
            ErrorMessage = "Repair or reinstall the local iGloo package, then go back one step and continue again.";
        }
        catch (CliTransportException ex) when (ex.Code == CliTransportError.ProcessStartFailed)
        {
            // Process.Start failed before any request bytes could reach the CLI.
            _logger.LogError(ex, "Packaged CLI could not start an ISO request");
            if (!IsCurrentAcquisition(distro.Id, generation))
                return;
            HasError = true;
            ErrorTitle = "Check local iGloo CLI";
            ErrorMessage = "Check that the local iGloo CLI can start, then go back one step and continue again. " +
                           "Repair the iGloo package if the problem continues.";
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
            HasError = true;
            (ErrorTitle, ErrorMessage) = CommandFailureAction(commandFailure?.Code,
                commandFailure?.DetailCode);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeAcquisitions) == 0)
                IsRunning = false;
        }
    }

    private static (string Title, string Message) CommandFailureAction(CommandErrorCode? code,
        string? detailCode) =>
        (code, detailCode) switch
        {
            (CommandErrorCode.InternalFailure, "IsoNetworkUnavailable") => (
                "Check your connection",
                "Check your internet connection, then go back one step and continue again."),
            (CommandErrorCode.InternalFailure, "IsoCacheUnavailable") => (
                "Check the download folder",
                "Check available storage and access to iGloo's download folder, " +
                "then go back one step and continue again."),
            (CommandErrorCode.VerificationFailed, "IsoVerificationFailed") => (
                "Retry image verification",
                "Go back one step and continue again; iGloo will fetch and verify the image again. " +
                "If verification fails again, use Back to choose another distribution."),
            (CommandErrorCode.VerificationFailed, "IsoSourceMetadataUnavailable") => (
                "Update iGloo or choose another distribution",
                "Install an updated iGloo package and reopen it, then retry. " +
                "To continue now, use Back to choose another distribution."),
            _ => CommandFailureActionForCode(code),
        };

    private static (string Title, string Message) CommandFailureActionForCode(
        CommandErrorCode? code) => code switch
        {
            CommandErrorCode.VerificationFailed => (
                "Retry image verification",
                "Go back one step and continue again; iGloo will fetch and verify the image again. " +
                "If verification fails again, use Back to choose another distribution."),
            CommandErrorCode.ResourceBusy => (
                "Wait for the other transfer",
                "Wait for the other iGloo image transfer to finish, then go back one step and continue again."),
            CommandErrorCode.NotSupported => (
                "Choose another distribution",
                "Use Back to choose a distribution supported by this iGloo package."),
            CommandErrorCode.InvalidRequest or CommandErrorCode.UnsupportedCapability or
                CommandErrorCode.UnsupportedVersion => (
                "Repair local iGloo package",
                "Repair or reinstall the local iGloo package, then go back one step and continue again."),
            _ => (
                "Download failed",
                "Check your connection and available storage, then go back one step and continue again. " +
                "If this keeps happening, use Back to choose another distribution."),
        };

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
                _unresolvedOperations.Remove(distroId);
                OperationToReview = null;
                RetryBlocked = false;
                ErrorTitle = "Download failed";
                ErrorMessage = "Go back one step and continue again to verify the cached image.";
            }
            else
            {
                BlockRetryUntilStatusReview(unresolved);
                ErrorMessage = "Check the iGloo operation log, then go back one step and continue again. " +
                               "The previous transfer has no confirmed terminal state.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentReview(distroId, generation))
                return;
            ErrorMessage = "Go back one step and continue again before starting another transfer.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ISO operation status check failed");
            if (!IsCurrentReview(distroId, generation))
                return;
            ErrorMessage = "Check that the local iGloo CLI is available, then select " +
                           "go back one step and continue again.";
        }
    }

    private void BlockRetryUntilStatusReview(Guid? operationId)
    {
        _reviewGeneration++;
        if (Distro is { } distro)
            _unresolvedOperations[distro.Id] = operationId;
        RetryBlocked = true;
        OperationToReview = operationId is { } id && id != Guid.Empty ? id : null;
        ErrorTitle = OperationToReview is null
            ? "Review the download log"
            : "Download failed";
        ErrorMessage = OperationToReview is { } known
            ? $"Go back one step and continue again. iGloo could not confirm operation {known:D}."
            : "Open the local iGloo log and review the unfinished download before starting another transfer.";
        HasError = true;
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
