using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>Volume Shadow Copy snapshots of a lettered local volume, made and removed through Windows' own provider.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeSnapshots(ILogger<WindowsVolumeSnapshots> logger) : IVolumeSnapshots
{
    private const string Scope = @"root\cimv2";

    public IVolumeSnapshot? TryCreate(string pathOnVolume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOnVolume);
        var root = VolumeRoot(pathOnVolume);
        if (root is null)
        {
            logger.LogInformation("No snapshot: {Path} is not on a lettered local volume", pathOnVolume);
            return null;
        }
        try
        {
            HostProtection.RequireWritable("volume snapshot");
            using var shadows = new ManagementClass(Scope, "Win32_ShadowCopy", null);
            using var input = shadows.GetMethodParameters("Create");
            input["Volume"] = root;
            // Kept until it is deleted, and made without asking applications to flush.
            input["Context"] = "ClientAccessible";
            using var result = shadows.InvokeMethod("Create", input, null);
            var code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            if (code != 0 || result["ShadowID"] is not string id)
            {
                logger.LogWarning("Windows did not make a snapshot of {Volume} (code {Code})", root, code);
                return null;
            }

            using var shadow = new ManagementObject(Scope, $"Win32_ShadowCopy.ID=\"{id}\"", null);
            shadow.Get();
            if (shadow["DeviceObject"] is not string device || device.Length == 0)
            {
                Delete(id, logger);
                return null;
            }
            logger.LogInformation("Snapshot {Id} of {Volume} made at {Device}", id, root, device);
            return new Snapshot(root, device, id, logger);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                       or InvalidCastException or FormatException or HostProtectedException)
        {
            logger.LogWarning(ex, "Windows could not make a snapshot of {Volume}", root);
            return null;
        }
    }

    /// <summary>"C:\" for a path on a lettered volume; null for a network or device path.</summary>
    internal static string? VolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' ? root.ToUpperInvariant() : null;
    }

    /// <summary>The path below the snapshot device that corresponds to a path on the live volume.</summary>
    internal static string MapPath(string volumeRoot, string device, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path is not on the volume of this snapshot.", nameof(path));
        return device.TrimEnd('\\') + "\\" + full[volumeRoot.Length..];
    }

    private static void Delete(string id, ILogger logger)
    {
        try
        {
            HostProtection.RequireWritable("volume snapshot removal");
            using var shadow = new ManagementObject(Scope, $"Win32_ShadowCopy.ID=\"{id}\"", null);
            shadow.Delete();
            logger.LogInformation("Snapshot {Id} removed", id);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                       or HostProtectedException)
        {
            // It takes disk space until Windows or the user removes it.
            logger.LogWarning(ex, "Snapshot {Id} could not be removed; 'vssadmin delete shadows' with that id removes it", id);
        }
    }

    private sealed class Snapshot(string root, string device, string id, ILogger logger) : IVolumeSnapshot
    {
        private bool _removed;

        public string Map(string path) => MapPath(root, device, path);

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            Delete(id, logger);
        }
    }
}
