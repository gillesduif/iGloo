using System.Text.Json;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.Core.Plugins;

/// <summary>
/// Scans a <c>distros/</c> directory tree, reads every <c>distro.json</c> manifest it finds,
/// and exposes the results as <see cref="LoadedDistros"/>.
///
/// Directories whose names start with an underscore (e.g. <c>_template</c>, <c>_schema</c>)
/// are skipped. A single malformed manifest emits a warning and does not abort loading.
/// </summary>
public sealed partial class DistroLoader
{
    private readonly ILogger<DistroLoader> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private List<DistroManifest> _distros = [];

    public DistroLoader(ILogger<DistroLoader> logger) => _logger = logger;

    public IReadOnlyList<DistroManifest> LoadedDistros => _distros;

    /// <summary>
    /// Loads (or reloads) all distro manifests from <paramref name="distrosDirectory"/>.
    /// Safe to call multiple times; replaces the previous result set.
    /// </summary>
    public void Load(string distrosDirectory)
    {
        if (!Directory.Exists(distrosDirectory))
        {
            LogDistrosDirectoryNotFound(distrosDirectory);
            _distros = [];
            return;
        }

        var loaded = new List<DistroManifest>();

        foreach (var dir in Directory.EnumerateDirectories(distrosDirectory))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('_'))
                continue;

            var manifestPath = Path.Combine(dir, "distro.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<DistroManifest>(json, JsonOpts);
                if (manifest is null)
                    continue;

                // Stamp the source directory so relative asset paths (logo, screenshots)
                // can be resolved by consumers.
                manifest = manifest with { SourceDirectory = Path.GetFullPath(dir) };

                if (!string.Equals(manifest.Id, name, StringComparison.OrdinalIgnoreCase))
                    LogIdFolderMismatch(manifest.Id, name);

                loaded.Add(manifest);
                LogLoadedDistro(manifest.Id, manifest.DisplayName);
            }
            // A single malformed manifest must not abort the whole catalog: these are the
            // only failures File.ReadAllText + JsonSerializer.Deserialize can raise here.
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogManifestLoadFailed(ex, manifestPath);
            }
        }

        _distros = loaded;
        LogCatalogLoaded(loaded.Count);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distros directory not found: {Dir}")]
    private partial void LogDistrosDirectoryNotFound(string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distro id '{Id}' does not match folder name '{Folder}'")]
    private partial void LogIdFolderMismatch(string id, string folder);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded distro: {Id} ({DisplayName})")]
    private partial void LogLoadedDistro(string id, string displayName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load distro manifest from {Path}")]
    private partial void LogManifestLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Distro catalog loaded: {Count} distro(s)")]
    private partial void LogCatalogLoaded(int count);
}
