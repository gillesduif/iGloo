using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Preflight;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the File Staging wizard step.
///
/// Orchestrates four sequential phases:
///   1. Scanning   - enumerate files in the selected folders.
///   2. Copying    - stream files to the staging directory with progress.
///   3. Generating - build the migration manifest, render the kickstart (via the distro plugin),
///                   and write the first-boot agent to the staging directory.
///   4. Complete   - all artefacts are in place; the user can proceed to USB creation (M5).
/// </summary>
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

    /// <summary>
    /// Stores the user's choices and resets all observable state.
    /// Call this before navigating to the staging step, then invoke <see cref="StageCommand"/>.
    /// </summary>
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

            var userSetup = new UserSetup
            {
                WindowsUsername = _setup.WindowsUsername,
                LinuxUsername = _setup.LinuxUsername,
                LinuxPassword = _setup.LinuxPassword,
                Locale = "en_US.UTF-8",
                Timezone = _setup.Timezone,
                Keymap = _setup.Keymap,
                SelectedFolderNames = _setup.GetSelectedFolderNames(),
                SelectedFolders = _setup.GetSelectedFolders(),
                SelectedBrowserNames = _setup.GetSelectedBrowserNames(),
                SelectedBrowsers = _setup.GetSelectedBrowsers(),
                SuggestedPackages = _setup.GetSelectedSuggestions(),
                WifiNetworks = wifiNetworks,
            };

            var manifest = ManifestGeneratorService.Generate(
                _distroId, userSetup, _preflightReport, stagingResult,
                _targetDisk, _installMode, _linuxSizeGb);

            var manifestPath = Path.Combine(stagingResult.StagingDirectory, "migration-manifest.json");
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
                var ksPath = Path.Combine(stagingResult.StagingDirectory, installerConfig.FileName);
                await File.WriteAllBytesAsync(ksPath, installerConfig.Contents, ct);
                _logger.LogInformation("Installer config written to {Path}", ksPath);

                foreach (var extra in installerConfig.Extras)
                {
                    var extraPath = Path.Combine(stagingResult.StagingDirectory, extra.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
                    await File.WriteAllBytesAsync(extraPath, extra.Contents, ct);
                }

                // First-boot agent files.
                var agentPayload = await plugin.GetAgentPayloadAsync(ct);
                var agentDir = Path.Combine(stagingResult.StagingDirectory, "igloo-agent");
                Directory.CreateDirectory(agentDir);

                foreach (var agentFile in agentPayload.Files)
                {
                    var filePath = Path.Combine(agentDir, agentFile.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    await File.WriteAllBytesAsync(filePath, agentFile.Contents, ct);
                }

                _logger.LogInformation("Agent payload written to {Dir}", agentDir);
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
}
