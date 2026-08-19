using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Migration.Chromium;
using Igloo.Preflight;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class FileStagingViewModel : ObservableObject
{
    private readonly IFileStagingService _stagingService;
    private readonly DistroRegistry _registry;
    private readonly ILogger<FileStagingViewModel> _logger;

    private FileStagingRequest? _request;
    private string? _distroId;
    private PreflightReport? _preflightReport;
    private MigrationSetupViewModel? _setup;
    private DiskInfo? _targetDisk;
    private DiskInstallMode _installMode = DiskInstallMode.ReplaceDisk;
    private int _linuxSizeGb;

    private static readonly JsonSerializerOptions PrettyJson =
        new() { WriteIndented = true };

    // A plugin skips any file it cannot find on disk, so a missing one is silent.
    // These four carry behaviour the user will notice.
    private static readonly string[] RequiredAgentFiles =
        ["agent.py", "igloo_boot.py",
         "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz"];

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
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private FileStagingResult? _result;

    //   Derived                                

    public double ProgressPercent => BytesTotal > 0 ? BytesCopied * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;

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
        IFileStagingService stagingService,
        DistroRegistry registry,
        ILogger<FileStagingViewModel> logger)
    {
        _stagingService = stagingService;
        _registry = registry;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   

    public void Prepare(
        MigrationSetupViewModel setup,
        PreflightReport report,
        DistroManifest distro,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.ReplaceDisk,
        int linuxSizeGb = 0)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(distro);

        _setup = setup;
        _preflightReport = report;
        _distroId = distro.Id;
        _targetDisk = targetDisk;
        _installMode = installMode;
        _linuxSizeGb = linuxSizeGb;
        _request = new FileStagingRequest(distro.Id, setup.GetSelectedFolderPaths());

        IsComplete = false;
        HasError = false;
        ErrorMessage = null;
        Result = null;
        BytesCopied = 0;
        BytesTotal = 0;
        Phase = FileStagingPhase.Scanning;
        CurrentFile = null;
    }

    //   Command                                

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StageAsync(CancellationToken ct)
    {
        if (_request is null || _setup is null || _preflightReport is null || _distroId is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;

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
            //   Step 1: Copy files                      ─
            var stagingResult = await _stagingService.StageAsync(_request, progress, ct);

            //   Step 2: Generate migration manifest              
            Phase = FileStagingPhase.Generating;
            OnPropertyChanged(nameof(PhaseDisplay));

            // Export saved Wi-Fi networks (netsh spawns a process - keep it off
            // the UI thread). Defensive: the scanner never throws, returns [].
            var wifiNetworks = await Task.Run(WindowsWifiScanner.Scan, ct);
            _logger.LogInformation("Detected {Count} saved Wi-Fi network(s) for migration",
                wifiNetworks.Count);

            // Chromium password migration (Phase 2, ADR-011): decrypt on
            // Windows (DPAPI only unlocks in this user's session) and attach
            // re-encrypted credential envelopes to the manifest entries.
            // Off the UI thread: SQLite reads plus a 600k-iteration PBKDF2
            // per browser. Fail-soft: any browser that cannot be migrated
            // keeps its recorded-only entry.
            var selectedBrowsers = await Task.Run(
                () => BrowserCredentialMigration.AttachCredentials(
                    _setup.GetSelectedBrowsers(), _setup.LinuxPassword, _logger), ct);

            var userSetup = new UserSetup
            {
                WindowsUsername = _setup.WindowsUsername,
                LinuxUsername = _setup.LinuxUsername,
                LinuxPassword = _setup.LinuxPassword,
                Locale = _setup.Locale,
                Timezone = _setup.Timezone,
                Keymap = _setup.Keymap,
                SelectedFolderNames = _setup.GetSelectedFolderNames(),
                SelectedFolders = _setup.GetSelectedFolders(),
                SelectedBrowserNames = _setup.GetSelectedBrowserNames(),
                SelectedBrowsers = selectedBrowsers,
                SuggestedPackages = _setup.GetSelectedSuggestions(),
                WifiNetworks = wifiNetworks,
            };

            var manifest = ManifestGeneratorService.Generate(
                _distroId, userSetup, _preflightReport, stagingResult,
                _targetDisk, _installMode, _linuxSizeGb);

            // Wallpaper migration: locate the current desktop image and carry it
            // next to the manifest (staging root -> seed partition root, mirrored
            // by both the USB writer and the direct-install artefact copy).
            // Fail-soft: no image simply means no "wallpaper" key in the manifest.
            manifest = await Task.Run(
                () => AttachWallpaper(manifest, stagingResult.StagingDirectory), ct);
            manifest = await Task.Run(
                () => AttachAccountPicture(manifest, stagingResult.StagingDirectory), ct);

            var manifestPath = Path.Join(stagingResult.StagingDirectory, "migration-manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, PrettyJson),
                ct);
            _logger.LogInformation("Migration manifest written to {Path}", manifestPath);

            //   Step 3: Plugin renders installer config + agent        
            if (_registry.TryGet(_distroId, out var plugin))
            {
                // Kickstart (or preseed / Calamares config, depending on the distro).
                var installerConfig = await plugin.RenderInstallerConfigAsync(manifest, ct);
                var ksPath = Path.Join(stagingResult.StagingDirectory, installerConfig.FileName);
                await File.WriteAllBytesAsync(ksPath, installerConfig.Contents, ct);
                _logger.LogInformation("Installer config written to {Path}", ksPath);

                foreach (var extra in installerConfig.Extras)
                {
                    var extraPath = Path.Join(stagingResult.StagingDirectory, extra.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
                    await File.WriteAllBytesAsync(extraPath, extra.Contents, ct);
                }

                // First-boot agent files.
                var agentPayload = await plugin.GetAgentPayloadAsync(ct);
                var agentDir = Path.Join(stagingResult.StagingDirectory, "igloo-agent");
                Directory.CreateDirectory(agentDir);

                foreach (var agentFile in agentPayload.Files)
                {
                    var filePath = Path.Join(agentDir, agentFile.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await File.WriteAllBytesAsync(filePath, agentFile.Contents, ct);
                }

                var staged = agentPayload.Files.Select(f => f.RelativePath).ToList();
                _logger.LogInformation("Agent payload written to {Dir}: {Files}",
                    agentDir, string.Join(", ", staged));

                foreach (var required in RequiredAgentFiles
                             .Where(f => !staged.Contains(f, StringComparer.OrdinalIgnoreCase)))
                {
                        _logger.LogError(
                            "Agent payload is missing {File} - the plugin could not find it. " +
                            "The first boot will run without it.", required);
                }
            }
            else
            {
                _logger.LogWarning(
                    "No plugin found for distro '{Id}' - installer config not generated. " +
                    "Ensure Igloo.Distro.FedoraKde.dll is present in the distro folder.",
                    _distroId);
            }

            Result = stagingResult;
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File staging cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "File staging failed");
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    //   Property-change hooks                         ─

    partial void OnPhaseChanged(FileStagingPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));

    /// <summary>
    /// Copies the current Windows wallpaper into the staging root as
    /// <c>igloo-wallpaper.&lt;ext&gt;</c> and points the manifest at it. Returns the
    /// manifest unchanged when there is no migratable image - never throws.
    /// </summary>
    private MigrationManifest AttachWallpaper(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = WallpaperReader.TryFindWallpaper(_logger);
            if (source is null)
                return manifest;

            // The TranscodedWallpaper fallback has no extension but is JPEG content.
            var ext = Path.GetExtension(source);
            if (string.IsNullOrEmpty(ext))
                ext = ".jpg";
            var fileName = $"igloo-wallpaper{ext}";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Wallpaper staged from {Source} as {FileName}", source, fileName);

            return manifest with
            {
                Wallpaper = new WallpaperMigration { FileName = fileName, OriginalPath = source },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the wallpaper - skipping (non-fatal)");
            return manifest;
        }
    }

    /// <summary>
    /// Copies the Windows account picture next to the manifest as
    /// <c>igloo-avatar.jpg</c> and points the manifest at it. Returns the manifest
    /// unchanged when the user never set one - never throws.
    /// </summary>
    private MigrationManifest AttachAccountPicture(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = AccountPictureReader.TryFindAccountPicture(_logger);
            if (source is null)
                return manifest;

            // Windows stores these as JPEG regardless of what the user imported.
            const string fileName = "igloo-avatar.jpg";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Account picture staged from {Source}", source);

            return manifest with
            {
                AccountPicture = new AccountPictureMigration
                {
                    FileName = fileName,
                    OriginalPath = source,
                },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the account picture - skipping (non-fatal)");
            return manifest;
        }
    }
}
