using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Runtime;

namespace Igloo.Core.Runtime.Execution;

/// <summary>Private cache location and provenance. Public callers see only content identity.</summary>
public sealed class FileArtifactRegistry(string directory, string cacheRoot)
{
    public static string DefaultDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "artifacts");
    public async Task<ArtifactAcquired> RecordAsync(string distroId, IsoAcquisitionResult acquired,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(acquired);
        if (!acquired.Sha256Verified || acquired.VerifiedSha256 is not { Length: 64 } digest ||
            !digest.All(Uri.IsHexDigit) || acquired.SizeBytes < 0)
            throw new ArtifactVerificationException();
        var path = RequireCachePath(acquired.LocalPath);
        if (!File.Exists(path) || new FileInfo(path).Length != acquired.SizeBytes)
            throw new ArtifactVerificationException();
        var normalizedHash = digest.ToUpperInvariant();
        // Close the verification-to-registration gap. Later uses must still reverify.
        using (var current = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(current, cancellationToken)
                .ConfigureAwait(false));
            if (!string.Equals(actual, normalizedHash, StringComparison.Ordinal))
                throw new ArtifactVerificationException();
        }
        var id = IdFor(distroId, normalizedHash);
        var record = new ArtifactRecord(1, id, distroId, normalizedHash, path,
            acquired.SizeBytes, acquired.GpgVerified, DateTimeOffset.UtcNow, null);
        var sealedRecord = record with { IntegritySha256 = Integrity(record) };
        Directory.CreateDirectory(directory);
        using var lease = await ResourceLock.AcquireAsync("artifact-index:" + id,
            cancellationToken).ConfigureAwait(false);
        var destination = RecordPath(id);
        if (File.Exists(destination))
        {
            var prior = await ReadAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new ArtifactVerificationException();
            if (prior.DistroId != distroId || prior.Sha256 != normalizedHash)
                throw new ArtifactVerificationException();
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, sealedRecord, CommandProtocol.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(id, distroId, normalizedHash, acquired.GpgVerified, acquired.SizeBytes);
    }

    public async Task<ArtifactStatus?> InspectAsync(string artifactId,
        CancellationToken cancellationToken = default)
    {
        var record = await ReadAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        using var lease = await ResourceLock.AcquireAsync("iso:" + record.DistroId,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(record.LocalPath))
            return new(record.ArtifactId, record.DistroId, record.Sha256,
                record.GpgVerified, record.SizeBytes, false, false);
        var size = new FileInfo(record.LocalPath).Length;
        if (size != record.SizeBytes)
            return new(record.ArtifactId, record.DistroId, record.Sha256,
                record.GpgVerified, record.SizeBytes, true, false);
        using var stream = new FileStream(record.LocalPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
        return new(record.ArtifactId, record.DistroId, record.Sha256,
            record.GpgVerified, record.SizeBytes, true,
            string.Equals(hash, record.Sha256, StringComparison.Ordinal));
    }

    private async Task<ArtifactRecord?> ReadAsync(string artifactId, CancellationToken cancellationToken)
    {
        if (!IsValidId(artifactId)) throw new ArgumentException("Invalid artifact ID.");
        var path = RecordPath(artifactId);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        ArtifactRecord? record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<ArtifactRecord>(stream,
                CommandProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ArtifactVerificationException();
        }
        if (record is null || record.SchemaVersion != 1 || record.DistroId is null ||
            record.Sha256 is not { Length: 64 } || !record.Sha256.All(Uri.IsHexDigit) ||
            record.LocalPath is null || record.SizeBytes < 0 ||
            record.ArtifactId != artifactId ||
            record.ArtifactId != IdFor(record.DistroId, record.Sha256) ||
            !string.Equals(record.IntegritySha256, Integrity(record with { IntegritySha256 = null }),
                StringComparison.Ordinal) ||
            RequireCachePath(record.LocalPath) != record.LocalPath)
            throw new ArtifactVerificationException();
        return record;
    }

    private string RequireCachePath(string path)
    {
        try
        {
            var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArtifactVerificationException();
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new ArtifactVerificationException();
        }
    }

    private string RecordPath(string id) => Path.Join(directory, id + ".json");

    private static string IdFor(string distroId, string digest) => "sha256-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(distroId + "\n" + digest)));

    private static bool IsValidId(string? id) => id is { Length: 71 } &&
        id.StartsWith("sha256-", StringComparison.Ordinal) && id.AsSpan(7).ToArray().All(Uri.IsHexDigit);

    private static string Integrity(ArtifactRecord record) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(record, CommandProtocol.JsonOptions)));

    private sealed record ArtifactRecord(int SchemaVersion, string ArtifactId, string DistroId,
        string Sha256, string LocalPath, long SizeBytes, bool GpgVerified,
        DateTimeOffset VerifiedAt, string? IntegritySha256);
}

public sealed class ArtifactStatusHandler(FileArtifactRegistry registry) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("artifact.status", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<ArtifactSelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(selection?.ArtifactId))
            throw new ArgumentException("Artifact ID is required.");
        return await registry.InspectAsync(selection.ArtifactId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException();
    }
}
