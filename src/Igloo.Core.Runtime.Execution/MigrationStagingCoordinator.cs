using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Migration;
using Igloo.Migration.Chromium;
using Igloo.Preflight;
using Microsoft.Extensions.Logging;

namespace Igloo.Core.Runtime.Execution;

/// <summary>Local in-process staging workflow. Sensitive input never enters the public JSON contract.</summary>
public sealed record MigrationStagingInput(FileStagingRequest Files, UserSetup User,
    PreflightReport ObservedMachine, DiskInfo? TargetDisk, DiskInstallMode InstallMode,
    int LinuxSizeGb)
{
    public bool IncludeWifiNetworks { get; init; } = true;
}

internal sealed record MigrationStagingHooks(
    Func<CancellationToken, Task<IReadOnlyList<WifiNetwork>>> ScanWifi,
    Func<IReadOnlyList<BrowserMigration>, string?, CancellationToken, Task<IReadOnlyList<BrowserMigration>>> AttachBrowsers,
    Func<MigrationManifest, string, MigrationManifest> AttachWallpaper,
    Func<MigrationManifest, string, MigrationManifest> AttachAccountPicture);

public sealed class MigrationStagingCoordinator
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly IFileStagingService _staging;
    private readonly DistroRegistry _registry;
    private readonly IPreflightChecker _checker;
    private readonly ILogger<MigrationStagingCoordinator> _logger;
    private readonly MigrationStagingHooks _hooks;

    public MigrationStagingCoordinator(IFileStagingService staging, DistroRegistry registry,
        IPreflightChecker checker, ILogger<MigrationStagingCoordinator> logger)
        : this(staging, registry, checker, logger, null) { }

    internal MigrationStagingCoordinator(IFileStagingService staging, DistroRegistry registry,
        IPreflightChecker checker, ILogger<MigrationStagingCoordinator> logger,
        MigrationStagingHooks? hooks)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hooks = hooks ?? new(
            ct => Task.Run<IReadOnlyList<WifiNetwork>>(WindowsWifiScanner.Scan, ct),
            (browsers, password, ct) => Task.Run(() =>
                BrowserCredentialMigration.AttachCredentials(browsers, password, logger), ct),
            AttachWallpaper,
            AttachAccountPicture);
    }

    public async Task<FileStagingResult> StageAsync(MigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Files);
        ArgumentNullException.ThrowIfNull(input.User);
        ArgumentNullException.ThrowIfNull(input.ObservedMachine);
        ArgumentNullException.ThrowIfNull(input.Files.FolderPaths);
        ArgumentNullException.ThrowIfNull(input.Files.FolderNames);
        ArgumentNullException.ThrowIfNull(input.User.SelectedFolderNames);
        ArgumentNullException.ThrowIfNull(input.User.SelectedFolders);
        ArgumentNullException.ThrowIfNull(input.User.SelectedBrowserNames);
        ArgumentNullException.ThrowIfNull(input.User.SelectedBrowsers);
        if (!_registry.TryGet(input.Files.DistroId, out var plugin))
            throw new NotSupportedException("The selected distribution has no loaded installer provider.");

        if (input.User.SelectedFolders.Any(folder => folder is null) ||
            input.Files.FolderNames.Count != input.Files.FolderPaths.Count ||
            !input.Files.FolderNames.SequenceEqual(input.User.SelectedFolderNames,
                StringComparer.Ordinal) ||
            !input.Files.FolderNames.SequenceEqual(input.User.SelectedFolders.Select(f => f.Name),
                StringComparer.Ordinal))
            throw new InvalidOperationException("Selected folder sources no longer match the migration plan.");

        if (input.User.SelectedBrowsers.Any(b => b is null || b.SourceRelativePath is null ||
                (b.SourceRelativePath.Length != 0 && string.IsNullOrWhiteSpace(b.SourceRelativePath))))
            throw new InvalidOperationException("A selected browser has an invalid profile source path.");
        if (!input.User.SelectedBrowserNames.SequenceEqual(
                input.User.SelectedBrowsers.Select(b => b.Name), StringComparer.Ordinal))
            throw new InvalidOperationException("Selected browser profiles no longer match the migration plan.");
        // A selected browser can be recorded without a portable profile path.
        // Preserve that manifest entry for provider-specific compatibility or
        // credential handling; only resolved profile roots are copied.
        var browserSources = input.User.SelectedBrowsers.Select(b => b.SourceRelativePath)
            .Where(path => path.Length != 0).ToArray();
        var browserPaths = browserSources.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
            path => path, FileStagingService.BrowserProfileStagingPath,
            StringComparer.OrdinalIgnoreCase);
        // The wizard's preflight is a selection snapshot, not manifest authority.
        // Reobserve in Core immediately before copying private data, and bind a
        // selected disk by its GPT identity to the current physical layout.
        var currentMachine = await _checker.RunAsync(cancellationToken).ConfigureAwait(false);
        if (currentMachine?.Disks is null)
            throw new InvalidOperationException("Current machine observation is unavailable.");
        var currentTarget = ResolveCurrentTarget(input.TargetDisk, currentMachine.Disks);
        var stagingRequest = input.Files with
        {
            BrowserRelativePaths = browserSources,
            BrowserProfiles = input.User.SelectedBrowsers
                .Where(browser => browser.SourceRelativePath.Length != 0).ToArray(),
        };
        var stagingResult = await _staging.StageAsync(stagingRequest, progress, cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Join(stagingResult.StagingDirectory, "migration-manifest.json");
        try
        {
            progress?.Report(new(FileStagingPhase.Generating, stagingResult.TotalBytesCopied,
                stagingResult.TotalBytesCopied, string.Empty));

            var wifiNetworks = input.IncludeWifiNetworks
                ? await _hooks.ScanWifi(cancellationToken).ConfigureAwait(false)
                : [];
            _logger.LogInformation("Detected {Count} saved Wi-Fi network(s) for migration", wifiNetworks.Count);
            // Selection metadata is not evidence that credentials were extracted.
            // Only the local extractor may add an encrypted credentials envelope;
            // the staging path is likewise derived from the copied profile root.
            var browserSelections = input.User.SelectedBrowsers.Select(browser => browser with
            {
                IncludesPasswords = false, CredentialsBlob = null, ProfileStagingPath = string.Empty
            }).ToArray();
            var selectedBrowsers = await _hooks.AttachBrowsers(browserSelections,
                input.User.LinuxPassword, cancellationToken).ConfigureAwait(false);
            if (selectedBrowsers.Count != input.User.SelectedBrowsers.Count ||
                selectedBrowsers.Where((b, i) =>
                    !string.Equals(b.Name, input.User.SelectedBrowsers[i].Name, StringComparison.Ordinal) ||
                    !string.Equals(b.Engine, input.User.SelectedBrowsers[i].Engine, StringComparison.Ordinal) ||
                    !string.Equals(b.SourceRelativePath, input.User.SelectedBrowsers[i].SourceRelativePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(b.DestRelativePath, input.User.SelectedBrowsers[i].DestRelativePath,
                        StringComparison.Ordinal)).Any())
                throw new InvalidOperationException("Selected browser profiles changed after staging.");
            var user = input.User with
            {
                SelectedBrowsers = selectedBrowsers.Select(b => b with
                {
                    ProfileStagingPath = b.SourceRelativePath.Length == 0
                        ? string.Empty : browserPaths[b.SourceRelativePath]
                }).ToArray(),
                WifiNetworks = wifiNetworks
            };
            var manifest = ManifestGeneratorService.Generate(input.Files.DistroId, user,
                currentMachine, stagingResult, currentTarget, input.InstallMode, input.LinuxSizeGb);
            manifest = await Task.Run(() => _hooks.AttachWallpaper(manifest, stagingResult.StagingDirectory),
                cancellationToken).ConfigureAwait(false);
            manifest = await Task.Run(() => _hooks.AttachAccountPicture(manifest, stagingResult.StagingDirectory),
                cancellationToken).ConfigureAwait(false);

            await PluginArtifactWriter.WriteAsync(plugin, manifest, stagingResult.StagingDirectory,
                _logger, cancellationToken).ConfigureAwait(false);
            var temporaryPath = Path.Join(stagingResult.StagingDirectory,
                ".migration-manifest-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PrettyJson);
                var output = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (output.ConfigureAwait(false))
                {
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, manifestPath);
            }
            finally
            {
                // A cancelled or failed write must never leave a partial manifest at
                // the name consumed by the installer. This is only our new temp file.
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception original)
        {
            try
            {
                FileStagingService.RemoveIncompleteGeneration(stagingResult.StagingDirectory,
                    stagingRequest.DistroId);
            }
            catch (Exception cleanup)
            {
                throw new IncompleteFileStagingCleanupException(stagingResult.StagingDirectory,
                    original, cleanup);
            }
            throw;
        }
        _logger.LogInformation("Migration manifest written to {Path}", manifestPath);
        return stagingResult;
    }

    private static DiskInfo? ResolveCurrentTarget(DiskInfo? selected,
        IReadOnlyList<DiskInfo> observedDisks)
    {
        if (selected is null) return null;
        if (selected.GptDiskGuid is not { } diskId || diskId == Guid.Empty ||
            !string.Equals(selected.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            selected.Partitions is null)
            throw new InvalidOperationException("The selected target has no canonical GPT identity.");
        var matches = observedDisks.Where(d => d.GptDiskGuid == diskId).Take(2).ToArray();
        // Staging copies files into the user's profile; it writes nothing to the target
        // disk. Only the disk's identity has to hold here. Its partition LAYOUT is
        // allowed to change between System Check and this point (the user extends C:,
        // removes a leftover installer partition, plugs in a USB stick that renumbers
        // nothing but refreshes flags) - that is revalidated where it matters, right
        // before the NTFS resize. A layout check here turned every such tidy-up into a
        // dead end on 2026-10-25.
        if (matches.Length != 1 || matches[0].Partitions is null ||
            !string.Equals(matches[0].PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
            matches[0].TotalBytes != selected.TotalBytes)
            throw new InvalidOperationException("The selected target disk is no longer present. Run the system check again.");
        return matches[0];
    }

    internal static bool LayoutUnchanged(DiskInfo selected, DiskInfo observed) =>
        selected.Partitions is not null && observed.Partitions is not null &&
        SameLayout(selected.Partitions, observed.Partitions);

    private static bool SameLayout(IReadOnlyList<PartitionInfo> selected,
        IReadOnlyList<PartitionInfo> observed) =>
        selected.Count == observed.Count &&
        selected.OrderBy(p => p.Index).Zip(observed.OrderBy(p => p.Index))
            .All(pair => pair.First.Index == pair.Second.Index &&
                         pair.First.OffsetBytes == pair.Second.OffsetBytes &&
                         pair.First.SizeBytes == pair.Second.SizeBytes &&
                         string.Equals(pair.First.GptType, pair.Second.GptType,
                             StringComparison.OrdinalIgnoreCase) &&
                         pair.First.IsSystem == pair.Second.IsSystem &&
                         pair.First.IsBoot == pair.Second.IsBoot);

    private MigrationManifest AttachWallpaper(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = WallpaperReader.TryFindWallpaper(_logger);
            if (source is null) return manifest;
            var ext = Path.GetExtension(source);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var fileName = $"igloo-wallpaper{ext}";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Wallpaper staged from {Source} as {FileName}", source, fileName);
            return manifest with { Wallpaper = new WallpaperMigration { FileName = fileName, OriginalPath = source } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the wallpaper - skipping (non-fatal)");
            return manifest;
        }
    }

    private MigrationManifest AttachAccountPicture(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = AccountPictureReader.TryFindAccountPicture(_logger);
            if (source is null) return manifest;
            const string fileName = "igloo-avatar.jpg";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Account picture staged from {Source}", source);
            return manifest with { AccountPicture = new AccountPictureMigration
            {
                FileName = fileName,
                OriginalPath = source,
            } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the account picture - skipping (non-fatal)");
            return manifest;
        }
    }
}
