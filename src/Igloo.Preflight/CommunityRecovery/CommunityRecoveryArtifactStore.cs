namespace Igloo.Preflight.CommunityRecovery;

public interface ICommunityRecoveryArtifactStore
{
    void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact);
    byte[] Reopen(Guid artifactId);
}

// Community-local storage. No Fleet execution state, overwrite, cleanup or restore operations.
public sealed class CommunityRecoveryArtifactStore(string root) : ICommunityRecoveryArtifactStore
{
    public const int MaximumArtifactBytes = 16 * 1024 * 1024;
    private readonly string _root = Path.GetFullPath(root);

    public static CommunityRecoveryArtifactStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iGloo", "RecoverySnapshots"));

    public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact)
    {
        var path = ArtifactPath(artifactId);
        if (artifact.IsEmpty || artifact.Length > MaximumArtifactBytes) throw new IOException("Recovery artifact size is invalid.");
        RejectReparseAncestors(_root);
        Directory.CreateDirectory(_root);
        RejectReparseAncestors(_root);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(artifact);
        stream.Flush(flushToDisk: true);
    }

    public byte[] Reopen(Guid artifactId)
    {
        var path = ArtifactPath(artifactId);
        RejectReparseAncestors(path);
        // Fresh handle, after the write handle was flushed and closed. No cached snapshot object.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumArtifactBytes) throw new IOException("Recovery artifact size is invalid.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Recovery artifact changed during reopen.");
        return bytes;
    }

    private string ArtifactPath(Guid id) => id != Guid.Empty
        ? Path.Combine(_root, id.ToString("D") + ".recovery.json")
        : throw new ArgumentException("Artifact identity is required.", nameof(id));

    private static void RejectReparseAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery artifact path contains a reparse point.");
        }
    }
}
