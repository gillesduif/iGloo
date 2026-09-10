using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Igloo.Core.Models;

namespace Igloo.Core.Services;

/// <summary>Strict opt-in reader and atomic publication in the existing migration manifest.</summary>
public static class InstallationTargetManifest
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static InstallationTargetClaim ReadClaim(string json, Guid expectedInstallationId, bool requireEsp = true)
    {
        using var document = Parse(json);
        ValidateEnvelope(document.RootElement, expectedInstallationId);
        if (!document.RootElement.TryGetProperty("installationTarget", out var target)
            || target.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Required installation target identity is unavailable in this manifest.");
        ValidateWireGuids(target);
        InstallationTargetClaim claim;
        try
        {
            claim = target.Deserialize<InstallationTargetClaim>()
                ?? throw new InvalidDataException("Installation target is missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed or incomplete installation target identity.", ex);
        }
        InstallationTargetValidation.ValidateClaim(claim, requireEsp);
        if (claim.InstallationId != expectedInstallationId)
            throw new InvalidDataException("Installation target belongs to another installation run.");
        return claim;
    }

    /// <summary>Check before any partition mutation; the returned digest binds publication to these bytes.</summary>
    public static async Task<string> ValidatePendingAsync(string path, Guid expectedInstallationId, CancellationToken ct = default)
    {
        var bytes = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
        ValidatePending(bytes, expectedInstallationId);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Publish a verified receipt without losing unrelated/forward-compatible manifest fields.
    /// On failure the caller must leave any created partition untouched and stop, never auto-retry.
    /// </summary>
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
        Justification = "Flush(true) is the durability barrier before atomic publication; FlushAsync does not request a disk flush.")]
    public static async Task WriteClaimAsync(string path, InstallationTargetClaim claim,
        string expectedManifestSha256, CancellationToken ct = default)
    {
        InstallationTargetValidation.ValidateClaim(claim, requireEsp: false);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestSha256);
        var original = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
        RequireDigest(original, expectedManifestSha256);
        ValidatePending(original, claim.InstallationId);
        var root = JsonNode.Parse(original)!.AsObject();
        root["installationTarget"] = JsonSerializer.SerializeToNode(claim);
        var content = Encoding.UTF8.GetBytes(root.ToJsonString(PrettyJson));
        // Also exercise the live-environment wire contract before publishing it.
        _ = ReadClaim(Encoding.UTF8.GetString(content), claim.InstallationId, requireEsp: false);
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (file.ConfigureAwait(false))
            {
                await file.WriteAsync(content, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            RequireDigest(await ReadBoundedAsync(fullPath, ct).ConfigureAwait(false), expectedManifestSha256);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidatePending(byte[] bytes, Guid expectedInstallationId)
    {
        string json;
        try { json = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Migration manifest is not valid UTF-8.", ex); }
        using var document = Parse(json);
        ValidateEnvelope(document.RootElement, expectedInstallationId);
        if (document.RootElement.TryGetProperty("installationTarget", out var target)
            && target.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("This manifest already has a target claim. Implicit reuse or retry is not authorized.");
    }

    private static JsonDocument Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new InvalidDataException("Missing or oversized migration manifest.");
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Migration manifest must be an object.");
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch (JsonException ex)
        {
            document?.Dispose();
            throw new InvalidDataException("Malformed migration manifest JSON.", ex);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void ValidateEnvelope(JsonElement root, Guid expectedInstallationId)
    {
        if (expectedInstallationId == Guid.Empty
            || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !root.TryGetProperty("installationId", out var id)
            || ReadCanonicalGuid(id) != expectedInstallationId)
            throw new InvalidDataException("Unsupported manifest version or unavailable/stale installation identity.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Duplicate manifest property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
    }

    private static void ValidateWireGuids(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "installationId" or "diskGuid" or "partitionGuid" or "gptType" or "rootPartitionGuid" or "espPartitionGuid")
                {
                    if (property.Name != "espPartitionGuid" || property.Value.ValueKind != JsonValueKind.Null)
                        _ = ReadCanonicalGuid(property.Value);
                }
                else
                    ValidateWireGuids(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                ValidateWireGuids(item);
    }

    private static Guid ReadCanonicalGuid(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || !Guid.TryParseExact(element.GetString(), "D", out var value)
            || value == Guid.Empty || !string.Equals(element.GetString(), value.ToString("D"), StringComparison.Ordinal))
            throw new InvalidDataException("GUIDs must be nonempty lowercase canonical GPT identities.");
        return value;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            if (file.Length > MaximumManifestBytes)
                throw new InvalidDataException("Oversized migration manifest.");
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }
    }

    private static void RequireDigest(byte[] bytes, string expected)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Migration manifest changed during target preparation; no claim was published.");
    }
}
