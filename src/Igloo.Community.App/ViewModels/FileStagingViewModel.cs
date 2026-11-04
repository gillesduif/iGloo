using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class FileStagingViewModel : ObservableObject
{
    private readonly IMigrationStagingWorkflow _coordinator;
    private readonly ILogger<FileStagingViewModel> _logger;
    private readonly string _stagingRoot;
    private readonly Action _openTaskManager;

    private FileStagingRequest? _request;
    private string? _distroId;
    private PreflightReport? _preflightReport;
    private MigrationSetupViewModel? _setup;
    private DiskInfo? _targetDisk;
    private DiskInstallMode _installMode = DiskInstallMode.DualBoot;
    private int _linuxSizeGb;

    //   Observable state                           

    [ObservableProperty] private FileStagingPhase _phase = FileStagingPhase.Scanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesCopied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesTotal;

    [ObservableProperty] private string? _currentFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStage))]
    [NotifyPropertyChangedFor(nameof(CanCheckStagingFolder))]
    [NotifyPropertyChangedFor(nameof(CanOpenTaskManager))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenTaskManagerCommand))]
    private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckStagingFolder))]
    [NotifyPropertyChangedFor(nameof(CanOpenTaskManager))]
    [NotifyCanExecuteChangedFor(nameof(OpenTaskManagerCommand))]
    private bool _hasError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenTaskManager))]
    [NotifyCanExecuteChangedFor(nameof(OpenTaskManagerCommand))]
    private bool _browserIsInUse;
    [ObservableProperty] private string _errorTitle = "Staging failed";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrphanDirectory))]
    [NotifyPropertyChangedFor(nameof(CanStage))]
    [NotifyPropertyChangedFor(nameof(CanCheckStagingFolder))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    private string? _orphanDirectory;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStage))]
    [NotifyPropertyChangedFor(nameof(CanCheckStagingFolder))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    private bool _recoveryStateUnknown;
    [ObservableProperty] private FileStagingResult? _result;

    //   Derived                                

    public double ProgressPercent => BytesTotal > 0 ? BytesCopied * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;
    public bool HasErrorDetail => !string.IsNullOrWhiteSpace(ErrorDetail);
    public bool HasOrphanDirectory => !string.IsNullOrWhiteSpace(OrphanDirectory);
    public bool CanStage => !IsRunning && !HasOrphanDirectory && !RecoveryStateUnknown;
    public bool CanCheckStagingFolder => HasError && !IsRunning &&
        (HasOrphanDirectory || RecoveryStateUnknown);
    public bool CanOpenTaskManager => BrowserIsInUse && HasError && !IsRunning;

    public string PhaseDisplay => Phase switch
    {
        FileStagingPhase.Scanning => "Scanning files…",
        FileStagingPhase.Copying => "Copying files…",
        FileStagingPhase.Generating => "Generating installer configuration…",
        FileStagingPhase.Complete => "Complete",
        _ => string.Empty,
    };

    //   Constructor                              

    public FileStagingViewModel(
        IMigrationStagingWorkflow coordinator,
        ILogger<FileStagingViewModel> logger)
        : this(coordinator, logger, Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging"))
    {
    }

    internal FileStagingViewModel(IMigrationStagingWorkflow coordinator,
        ILogger<FileStagingViewModel> logger, string stagingRoot, Action? openTaskManager = null)
    {
        _coordinator = coordinator;
        _logger = logger;
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _openTaskManager = openTaskManager ?? LaunchTaskManager;
    }

    //   API called by MainWindowViewModel                   

    public void Prepare(
        MigrationSetupViewModel setup,
        PreflightReport report,
        DistroManifest distro,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.DualBoot,
        int linuxSizeGb = 0)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(distro);
        if (installMode != DiskInstallMode.DualBoot)
            throw new ArgumentOutOfRangeException(nameof(installMode),
                "iGloo Community always installs alongside Windows.");

        _setup = setup;
        _preflightReport = report;
        _distroId = distro.Id;
        _targetDisk = targetDisk;
        _installMode = installMode;
        _linuxSizeGb = linuxSizeGb;
        _request = new FileStagingRequest(distro.Id, setup.GetSelectedFolderPaths())
        {
            FolderNames = setup.GetSelectedFolderNames(),
        };

        // Keep a known incomplete generation visible when the user navigates
        // away and back. StageAsync also scans the durable managed directory, so
        // an application restart cannot silently discard this guard.
        var needsReview = HasOrphanDirectory || RecoveryStateUnknown;
        IsComplete = false;
        BrowserIsInUse = false;
        HasError = needsReview;
        ErrorTitle = HasOrphanDirectory ? "Review incomplete migration files" :
            needsReview ? "Staging failed" : "Staging failed";
        ErrorMessage = HasOrphanDirectory
            ? "Review the incomplete migration folder below. After it is removed or moved, " +
              "go back one step and continue again."
            : RecoveryStateUnknown
                ? "Check that iGloo can read its local staging folder, " +
                  "then go back one step and continue again."
                : null;
        ErrorDetail = null;
        Result = null;
        BytesCopied = 0;
        BytesTotal = 0;
        Phase = FileStagingPhase.Scanning;
        CurrentFile = null;
    }

    //   Command                                

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStage))]
    private async Task StageAsync(CancellationToken ct)
    {
        if (_request is null || _setup is null || _preflightReport is null || _distroId is null)
            return;

        IsRunning = true;
        IsComplete = false;
        BrowserIsInUse = false;
        HasError = false;
        ErrorTitle = "Check selected files and storage";
        ErrorMessage = null;
        ErrorDetail = null;
        Result = null;
        BytesCopied = 0;
        BytesTotal = 0;
        CurrentFile = null;
        Phase = FileStagingPhase.Scanning;

        // The migration service leaves unique on-disk generations. One without its
        // final manifest is an interrupted copy made by iGloo itself under the
        // user's own AppData: remove it and carry on, as the alpha did. Only a
        // legacy (pre-generation) package that cannot be identified stops here.
        try
        {
            string? incomplete = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                incomplete = FindIncompleteGeneration(_stagingRoot);
                if (incomplete is null || !TryRemoveIncompleteGeneration(incomplete))
                    break;
            }
            if (incomplete is not null)
            {
                ShowIncompleteGeneration(incomplete);
                IsRunning = false;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not inspect migration staging generations");
            ShowUnknownRecoveryState();
            IsRunning = false;
            return;
        }

        var progress = new Progress<FileStagingProgress>(p =>
        {
            Phase = p.Phase;
            BytesCopied = p.BytesCopied;
            BytesTotal = p.BytesTotal;
            CurrentFile = p.CurrentItem;
            OnPropertyChanged(nameof(PhaseDisplay));
        });

        try
        {
            var userSetup = new UserSetup
            {
                WindowsUsername = _setup.WindowsUsername,
                WindowsComputerName = _setup.WindowsComputerName,
                LinuxUsername = _setup.LinuxUsername,
                LinuxPassword = _setup.LinuxPassword,
                Locale = _setup.Locale,
                Timezone = _setup.Timezone,
                Keymap = _setup.Keymap,
                SelectedFolderNames = _setup.GetSelectedFolderNames(),
                SelectedFolders = _setup.GetSelectedFolders(),
                SelectedBrowserNames = _setup.GetSelectedBrowserNames(),
                SelectedBrowsers = _setup.GetSelectedBrowsers(),
                SuggestedPackages = _setup.GetSelectedSuggestions(),
            };
            Result = await _coordinator.StageAsync(new LocalMigrationStagingInput(_request,
                userSetup, _preflightReport, _targetDisk, _installMode, _linuxSizeGb)
                { IncludeWifiNetworks = _setup.IsWifiMigrationAvailable && _setup.IncludeWifiNetworks },
                progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File staging cancelled");
            HasError = true;
            ErrorTitle = "Staging failed";
            ErrorMessage = "Go back one step and continue again to build a new migration package. " +
                           "The canceled package was not completed.";
        }
        catch (IncompleteFileStagingCleanupException ex)
        {
            _logger.LogError(ex, "Incomplete file staging cleanup failed");
            ShowIncompleteGeneration(ex.StagingDirectory);
            ErrorDetail = ex.ToString();
        }
        catch (BrowserProfileStagingException ex)
        {
            _logger.LogError(ex, "A selected browser profile could not be staged consistently");
            HasError = true;
            if (ex.Reason == BrowserProfileStagingFailure.RecoveryRequired)
            {
                ErrorTitle = "Open and close the selected browser";
                ErrorMessage = "A selected browser has an unfinished database update. Open the browsers selected " +
                               "for migration so they can finish recovery, then close them, including any background " +
                               "browser processes, and go back one step and continue again. iGloo has not changed your browser files.";
                ErrorDetail = null;
            }
            else if (ex.Reason == BrowserProfileStagingFailure.BrowserInUse)
            {
                BrowserIsInUse = true;
                var browserNames = _setup.GetSelectedBrowserNames();
                var selection = browserNames.Count > 0 ? string.Join(", ", browserNames) : "A selected browser";
                ErrorTitle = "Browser files are still in use";
                ErrorMessage = $"{selection} may still be running in the background after its windows close. " +
                               "Save your browser work and close its windows. If it remains running, open Task Manager (Ctrl+Shift+Esc) " +
                               "and end the remaining processes for the selected browsers, then go back one step and continue again. " +
                               "Your browser data is still selected for migration.";
                ErrorDetail = null;
            }
            else
            {
                ErrorTitle = "Close the selected browsers";
                ErrorMessage = "Close the browsers selected for migration, including any background browser processes, " +
                               "then go back one step and continue again. Your browser files have not been changed.";
                ErrorDetail = ex.ToString();
            }
        }
        catch (InvalidDataException ex) when (_distroId?.StartsWith("fedora-", StringComparison.OrdinalIgnoreCase) == true
            && ex.Message.Contains("Turn off saved Wi-Fi migration", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Fedora saved Wi-Fi migration was rejected before staging completed");
            HasError = true;
            ErrorTitle = "Turn off saved Wi-Fi migration";
            ErrorMessage = "Select Back until Configuration, turn off saved Wi-Fi migration, " +
                           "then continue to rebuild the migration package. " +
                           "Use wired Ethernet or an open Wi-Fi network for this Fedora netinstall.";
            ErrorDetail = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "File staging failed");
            HasError = true;
            ErrorTitle = "Staging failed";
            ErrorMessage = "Check the selected files and available space, then go back one step and continue again. " +
                           "Do not continue until a new migration package completes.";
            ErrorDetail = ex.ToString();
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTaskManager))]
    private void OpenTaskManager()
    {
        if (!CanOpenTaskManager) return;
        try { _openTaskManager(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not open Task Manager for browser migration");
            ErrorMessage = "Press Ctrl+Shift+Esc to open Task Manager. Save your browser work, close its windows, " +
                           "then end remaining background processes for the selected browsers and go back one step and continue again.";
        }
    }

    private static void LaunchTaskManager()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "Taskmgr.exe"))
        {
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void CheckStagingFolder()
    {
        if (!CanCheckStagingFolder)
            return;
        try
        {
            var incomplete = FindIncompleteGeneration(_stagingRoot);
            if (incomplete is not null)
            {
                ShowIncompleteGeneration(incomplete);
                return;
            }
            OrphanDirectory = null;
            RecoveryStateUnknown = false;
            ErrorTitle = "Staging failed";
            ErrorMessage = "Go back one step and continue again to build a new migration package.";
            ErrorDetail = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not recheck migration staging generations");
            ShowUnknownRecoveryState();
        }
    }

    // An interrupted generation is a copy iGloo made itself, directly under
    // <managed root>/<distro>/<guid>. Only that exact shape is removed; a legacy
    // package or anything unexpected is left for the user to review.
    private bool TryRemoveIncompleteGeneration(string directory)
    {
        var parent = Path.GetDirectoryName(directory);
        var distroName = parent is null ? null : Path.GetFileName(parent);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null || distroName is null || !IsSafeDistroName(distroName) ||
            !string.Equals(Path.GetDirectoryName(parent), _stagingRoot, comparison) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            (File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            return false;
        _logger.LogInformation("Removing an interrupted migration package from an earlier run: {Dir}", directory);
        Directory.Delete(directory, recursive: true);
        return true;
    }

    private void ShowIncompleteGeneration(string directory)
    {
        OrphanDirectory = directory;
        RecoveryStateUnknown = false;
        HasError = true;
        ErrorTitle = "Review incomplete migration files";
        ErrorMessage = "Open the folder below and review any copied data. " +
                       "After the folder is removed or moved, go back one step and continue again.";
    }

    private void ShowUnknownRecoveryState()
    {
        RecoveryStateUnknown = true;
        HasError = true;
        ErrorTitle = "Staging failed";
        ErrorMessage = "Check that iGloo can read its local staging folder, " +
                       "then go back one step and continue again.";
    }

    private static string? FindIncompleteGeneration(string root)
    {
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The managed staging root is not an ordinary directory.");

        var count = 0;
        foreach (var distro in Directory.EnumerateFileSystemEntries(root))
        {
            if (++count > 10_000)
                throw new IOException("The managed staging root has too many entries to review.");
            var distroName = Path.GetFileName(distro);
            var attributes = File.GetAttributes(distro);
            if (!IsSafeDistroName(distroName) ||
                (attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The managed staging root has an unknown entry.");

            // Previous releases wrote a completed package directly into the
            // distribution directory. Preserve it, and also inspect any newer
            // GUID generations beside it. An interrupted legacy package has no
            // trustworthy final manifest and must be reviewed before retrying.
            var generations = new List<string>();
            var hasLegacyEntries = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(distro))
            {
                if (++count > 10_000)
                    throw new IOException("The managed staging root has too many entries to review.");
                attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The managed staging folder has a redirected entry.");
                if (Guid.TryParseExact(Path.GetFileName(entry), "N", out _))
                {
                    if ((attributes & FileAttributes.Directory) == 0)
                        throw new IOException("The managed staging folder has an unknown generation.");
                    generations.Add(entry);
                }
                else
                {
                    hasLegacyEntries = true;
                }
            }
            if (hasLegacyEntries && !IsCompletedLegacyPackage(distro, distroName!))
                return distro;

            foreach (var generation in generations)
            {
                var manifest = Path.Join(generation, "migration-manifest.json");
                try
                {
                    attributes = File.GetAttributes(manifest);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return generation;
                }
                if ((attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The managed staging manifest has an unknown identity.");
            }
        }
        return null;
    }

    private static bool IsCompletedLegacyPackage(string directory, string distroId)
    {
        var manifestPath = Path.Join(directory, "migration-manifest.json");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(manifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;

        // The legacy writer committed this file last. Check its own binding so
        // an unrelated or partial directory cannot masquerade as a completed
        // package. No legacy package is removed or modified by this scan.
        var size = new FileInfo(manifestPath).Length;
        if (size is < 2 or > 64L * 1024 * 1024)
            return false;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("distroId", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), distroId, StringComparison.Ordinal) ||
                !root.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Object ||
                !files.TryGetProperty("stagingPath", out var path) ||
                path.ValueKind != JsonValueKind.String)
                return false;
            var recorded = path.GetString();
            if (string.IsNullOrWhiteSpace(recorded) || !Path.IsPathFullyQualified(recorded))
                return false;
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeDistroName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    //   Property-change hooks                         ─

    partial void OnPhaseChanged(FileStagingPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));
}
