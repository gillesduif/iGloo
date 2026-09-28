using System.Collections.Immutable;
using System.Text.Json;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianRootTransportChunkV1(int Index, string Name, long Length, string Sha256);
public sealed record DebianRootTransportV1(int Version, string Type, string DescriptorSha256,
    string ManifestSha256, Guid BuildId, Guid DerivationId, long ContentLength, string ContentSha256,
    long ChunkSize, ImmutableArray<DebianRootTransportChunkV1> Chunks);

// Physical transport only. Authentication remains the original descriptor's external authority.
public static class DebianRootTransports
{
    public const long ChunkSize = 1_073_741_824;
    public const long MaximumContentLength = 64 * ChunkSize;
    public const int MaximumManifestLength = 32768;

    public static DebianRootTransportV1 Reopen(ReadOnlySpan<byte> bytes, DebianConfiguredRootImportPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (bytes.Length is <= 0 or > MaximumManifestLength || plan.Transport != "Chunked" ||
            DebianConfiguredRootArtifacts.Digest(bytes) != plan.TransportManifestSha256)
            throw new InvalidDataException("Transport manifest binding mismatch.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        Fields(root, "Version", "Type", "DescriptorSha256", "ManifestSha256", "BuildId", "DerivationId",
            "ContentLength", "ContentSha256", "ChunkSize", "Chunks");
        if (Integer(root.GetProperty("Version")) != 1 || root.GetProperty("Type").GetString() != "Chunked" ||
            Integer(root.GetProperty("ContentLength")) != plan.ContentLength || plan.ContentLength is <= 0 or > MaximumContentLength ||
            Integer(root.GetProperty("ChunkSize")) != ChunkSize || root.GetProperty("BuildId").GetString() != plan.BuildId.ToString("D") ||
            root.GetProperty("DerivationId").GetString() != plan.DerivationId.ToString("D") ||
            root.GetProperty("DescriptorSha256").GetString() != plan.DescriptorSha256 ||
            root.GetProperty("ManifestSha256").GetString() != plan.ManifestSha256 || root.GetProperty("ContentSha256").GetString() != plan.ContentSha256)
            throw new InvalidDataException("Transport artifact identity mismatch.");
        var array = root.GetProperty("Chunks");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != (plan.ContentLength + ChunkSize - 1) / ChunkSize)
            throw new InvalidDataException("Transport chunk count mismatch.");
        var chunks = ImmutableArray.CreateBuilder<DebianRootTransportChunkV1>();
        foreach (var chunk in array.EnumerateArray())
        {
            Fields(chunk, "Index", "Name", "Length", "Sha256");
            var index = chunks.Count;
            var name = FormattableString.Invariant($"root.content.{index:D4}");
            var length = Math.Min(ChunkSize, plan.ContentLength - index * ChunkSize);
            var hash = chunk.GetProperty("Sha256").GetString();
            if (Integer(chunk.GetProperty("Index")) != index || chunk.GetProperty("Name").GetString() != name ||
                Integer(chunk.GetProperty("Length")) != length || !DebianDeploymentPlanning.Hash(hash) || hash!.Any(c => c is >= 'a' and <= 'f'))
                throw new InvalidDataException("Invalid transport chunk.");
            chunks.Add(new(index, name, length, hash!));
        }
        return new(1, "Chunked", plan.DescriptorSha256, plan.ManifestSha256, plan.BuildId, plan.DerivationId,
            plan.ContentLength, plan.ContentSha256, ChunkSize, chunks.ToImmutable());
    }

    private static long Integer(JsonElement value)
    {
        // Reject exponent/fraction notation as well as booleans and overflow, matching Python.
        if (value.ValueKind != JsonValueKind.Number || !value.GetRawText().All(char.IsAsciiDigit) || !value.TryGetInt64(out var result))
            throw new InvalidDataException("Transport integer required.");
        return result;
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Transport object required.");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.ToHashSet(StringComparer.Ordinal).SetEquals(names)) throw new InvalidDataException("Unknown, missing or duplicate transport field.");
    }
}
