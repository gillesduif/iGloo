using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Core.Plugins;

public sealed partial class DistroRegistry
{
    private readonly ILogger<DistroRegistry> _logger;
    private readonly Dictionary<string, IDistroPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public DistroRegistry(ILogger<DistroRegistry> logger)
    {
        _logger = logger;
    }

    
    public IReadOnlyDictionary<string, IDistroPlugin> Plugins => _plugins;

    
    public Task LoadAsync(string distrosDirectory, CancellationToken ct = default)
    {
        LogLoadingPlugins(distrosDirectory);

        if (!Directory.Exists(distrosDirectory))
        {
            LogDistrosDirectoryNotFound(distrosDirectory);
            return Task.CompletedTask;
        }

        foreach (var dir in Directory.EnumerateDirectories(distrosDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('_'))
                continue;

            // DLL naming convention: Igloo.Distro.{PascalCase}.dll
            var dllName = "Igloo.Distro." + ToPascalCase(folderName) + ".dll";
            var dllPath = Path.Join(dir, dllName);

            if (!File.Exists(dllPath))
            {
                LogNoPluginDll(dllPath);
                continue;
            }

            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

                var pluginType = assembly.GetTypes()
                    .FirstOrDefault(t =>
                        typeof(IDistroPlugin).IsAssignableFrom(t) &&
                        !t.IsAbstract &&
                        !t.IsInterface);

                if (pluginType is null)
                {
                    LogNoPluginImplementation(dllPath);
                    continue;
                }

                var plugin = (IDistroPlugin)Activator.CreateInstance(pluginType)!;
                _plugins[plugin.Id] = plugin;
                LogLoadedPlugin(plugin.Id, dllPath);
            }
            // One faulty plugin assembly must not abort discovery of the rest. This covers the
            // failure surface of LoadFromAssemblyPath + GetTypes + Activator.CreateInstance:
            // bad/locked images, unresolved dependencies, and constructor faults.
            catch (Exception ex) when (ex is IOException or BadImageFormatException
                or ReflectionTypeLoadException or TypeLoadException or MissingMemberException
                or TargetInvocationException or MemberAccessException or InvalidOperationException
                or NotSupportedException)
            {
                LogPluginLoadFailed(ex, dllPath);
            }
        }

        LogRegistryLoaded(_plugins.Count);
        return Task.CompletedTask;
    }

    
    public IDistroPlugin Get(string id)
        => _plugins.TryGetValue(id, out var plugin)
            ? plugin
            : throw new KeyNotFoundException($"No distro plugin registered with id '{id}'.");

    
    public bool TryGet(string id, [NotNullWhen(true)] out IDistroPlugin? plugin)
        => _plugins.TryGetValue(id, out plugin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loading distro plugins from {Dir}")]
    private partial void LogLoadingPlugins(string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distros directory not found: {Dir}")]
    private partial void LogDistrosDirectoryNotFound(string dir);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No plugin DLL at {Path}")]
    private partial void LogNoPluginDll(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No IDistroPlugin implementation found in {Dll}")]
    private partial void LogNoPluginImplementation(string dll);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded plugin '{Id}' from {Dll}")]
    private partial void LogLoadedPlugin(string id, string dll);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load plugin from {Dll}")]
    private partial void LogPluginLoadFailed(Exception ex, string dll);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin registry loaded: {Count} plugin(s)")]
    private partial void LogRegistryLoaded(int count);

    //   Helpers

    
    private static string ToPascalCase(string hyphenated)
        => string.Concat(hyphenated
            .Split('-')
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
}
