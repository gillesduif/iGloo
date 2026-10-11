using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Core.Runtime;

/// <summary>Provider binary and catalogue revision for advisory evidence, not a signature check.</summary>
public static class ProviderRevision
{
    public static async Task<string> FingerprintAsync(IDistroPlugin plugin,
        DistroManifest? loadedManifest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var assemblyPath = plugin.GetType().Assembly.Location;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(await File.ReadAllBytesAsync(assemblyPath, cancellationToken)
            .ConfigureAwait(false));
        var catalog = Path.Join(Path.GetDirectoryName(assemblyPath), "distro.json");
        if (File.Exists(catalog))
            hasher.AppendData(await File.ReadAllBytesAsync(catalog, cancellationToken)
                .ConfigureAwait(false));
        if (loadedManifest is not null)
            hasher.AppendData(JsonSerializer.SerializeToUtf8Bytes(loadedManifest,
                CommandProtocol.JsonOptions));
        return Convert.ToHexString(hasher.GetHashAndReset());
    }
}

public static class MigrationStateRevision
{
    public static string Bind(string observedMachineFingerprint, string providerFingerprint,
        string distroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedMachineFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            observedMachineFingerprint + "\n" + providerFingerprint + "\n" + distroId)));
    }
}
