using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent.Execution;

public interface IProtectedDirectoryAcl
{
    void CreateProtected(string path);
    bool Verify(string path);
    bool VerifyFile(string path);
}

public enum ProtectedStateStatus { Verified, Unavailable, IdentityMismatch, ArtifactMismatch, UnsafePath, AclRejected }
public sealed record ImmutableArtifact(string RelativePath, long Size, string Hash);
public sealed record ExecutionManifest(int SchemaVersion, ExecutionBinding Binding, ImmutableArray<ImmutableArtifact> Artifacts);
public sealed record ProtectedStateAuthority(ExecutionBinding Binding, string ManifestHash, int SchemaVersion = 1);
public sealed record ProtectedStateVerification(ProtectedStateStatus Status, ProtectedStateAuthority Authority);

public sealed partial class ProtectedExecutionState
{
    private static readonly string[] RootEntries = ["authority.json", "immutable", "manifest.json", "mutable"];
    private readonly string _root;
    private readonly IProtectedDirectoryAcl _acl;

    // Explicit root injection supports deterministic tests. Production uses ForMachine.
    public ProtectedExecutionState(string root, IProtectedDirectoryAcl acl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(acl);
        _root = Path.GetFullPath(root);
        _acl = acl;
    }

    public static ProtectedExecutionState ForMachine()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.IsPathFullyQualified(basePath)) throw new IOException("Machine-local execution root unavailable.");
        return new(Path.Combine(basePath, "iGloo-Fleet-Execution"), new WindowsProtectedDirectoryAcl());
    }

    public ProtectedStateAuthority Create(ExecutionBinding binding, IReadOnlyDictionary<string, byte[]> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(binding);
        if (!ExecutionAuthorizationRules.WellFormed(binding)) throw new InvalidDataException("Invalid execution binding.");
        if (artifacts.Keys.Any(k => !SafeName(k)) || artifacts.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Count)
            throw new InvalidDataException("Unsafe artifact path.");
        EnsureDirectory(_root);
        var endpoint = Path.Combine(_root, binding.Endpoint.AgentId.ToString("N"));
        EnsureDirectory(endpoint);
        var destination = Path.Combine(endpoint, binding.ExecutionId.ToString("N"));
        if (Directory.Exists(destination)) throw new IOException("Execution authority already exists.");
        var pending = Path.Combine(endpoint, ".pending-" + binding.ExecutionId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        EnsureDirectory(pending);
        EnsureDirectory(Path.Combine(pending, "immutable"));
        EnsureDirectory(Path.Combine(pending, "mutable"));
        var entries = ImmutableArray.CreateBuilder<ImmutableArtifact>();
        foreach (var (name, bytes) in artifacts.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            WriteNew(Path.Combine(pending, "immutable", name), bytes);
            entries.Add(new(name, bytes.LongLength, EvidenceIntegrity.HashBytes(bytes)));
        }
        var manifest = new ExecutionManifest(1, binding with { Operations = binding.Operations.OrderBy(o => o.OperationId).ToImmutableArray() }, entries.ToImmutable());
        var payload = EvidenceIntegrity.CanonicalBytes(manifest);
        WriteNew(Path.Combine(pending, "manifest.json"), payload);
        var authority = new ProtectedStateAuthority(binding, EvidenceIntegrity.HashBytes(payload));
        WriteNew(Path.Combine(pending, "authority.json"), EvidenceIntegrity.CanonicalBytes(authority));
        // A same-volume directory rename publishes the only canonical authority. A loser
        // leaves a non-authoritative pending directory for explicit operator cleanup.
        Directory.Move(pending, destination);
        if (Verify(authority).Status != ProtectedStateStatus.Verified) throw new IOException("Protected state verification failed.");
        return authority;
    }

    // Restart uses the separately ACL-protected receipt, never a freshly invented artifact hash.
    public ProtectedStateVerification Open(ExecutionBinding expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var unavailable = new ProtectedStateVerification(ProtectedStateStatus.Unavailable, new(expected, ""));
        if (!ExecutionAuthorizationRules.WellFormed(expected)) return unavailable;
        try
        {
            var endpoint = Path.Combine(_root, expected.Endpoint.AgentId.ToString("N"));
            var directory = Path.Combine(endpoint, expected.ExecutionId.ToString("N"));
            foreach (var path in new[] { _root, endpoint, directory })
                if (!NoReparseAncestors(path) || !_acl.Verify(path)) return unavailable;
            var receipt = Path.Combine(directory, "authority.json");
            if (!NoReparseAncestors(receipt) || !_acl.VerifyFile(receipt)) return unavailable;
            var authority = JsonSerializer.Deserialize<ProtectedStateAuthority>(File.ReadAllBytes(receipt));
            if (authority is null || !ExecutionAuthorizationRules.Equivalent(expected, authority.Binding))
                return unavailable with { Status = ProtectedStateStatus.IdentityMismatch };
            return Verify(authority);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { return unavailable; }
    }

    public ProtectedStateVerification Verify(ProtectedStateAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var status = ProtectedStateStatus.Unavailable;
        try { status = VerifyCore(authority); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { }
        return new(status, authority);
    }

    private ProtectedStateStatus VerifyCore(ProtectedStateAuthority authority)
    {
        if (authority.SchemaVersion != 1 || !ExecutionAuthorizationRules.WellFormed(authority.Binding)) return ProtectedStateStatus.IdentityMismatch;
        var endpoint = Path.Combine(_root, authority.Binding.Endpoint.AgentId.ToString("N"));
        var directory = Path.Combine(endpoint, authority.Binding.ExecutionId.ToString("N"));
        foreach (var path in new[] { _root, endpoint, directory, Path.Combine(directory, "immutable"), Path.Combine(directory, "mutable") })
        {
            if (!NoReparseAncestors(path)) return ProtectedStateStatus.UnsafePath;
            if (!_acl.Verify(path)) return ProtectedStateStatus.AclRejected;
        }
        var children = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!children.SequenceEqual(RootEntries)) return ProtectedStateStatus.ArtifactMismatch;
        var receiptPath = Path.Combine(directory, "authority.json");
        if (!NoReparseAncestors(receiptPath)) return ProtectedStateStatus.UnsafePath;
        if (!_acl.VerifyFile(receiptPath)) return ProtectedStateStatus.AclRejected;
        var receipt = JsonSerializer.Deserialize<ProtectedStateAuthority>(File.ReadAllBytes(receiptPath));
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.ManifestHash != authority.ManifestHash ||
            !ExecutionAuthorizationRules.Equivalent(receipt.Binding, authority.Binding)) return ProtectedStateStatus.IdentityMismatch;
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!NoReparseAncestors(manifestPath)) return ProtectedStateStatus.UnsafePath;
        if (!_acl.VerifyFile(manifestPath)) return ProtectedStateStatus.AclRejected;
        var payload = File.ReadAllBytes(manifestPath);
        if (EvidenceIntegrity.HashBytes(payload) != authority.ManifestHash) return ProtectedStateStatus.ArtifactMismatch;
        var manifest = JsonSerializer.Deserialize<ExecutionManifest>(payload);
        if (manifest is null || manifest.SchemaVersion != 1 || !ExecutionAuthorizationRules.Equivalent(manifest.Binding, authority.Binding))
            return ProtectedStateStatus.IdentityMismatch;
        if (manifest.Artifacts.IsDefault || manifest.Artifacts.Any(a => a is null || !SafeName(a.RelativePath)) ||
            manifest.Artifacts.Select(a => a.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Artifacts.Length)
            return ProtectedStateStatus.UnsafePath;
        var immutable = Path.Combine(directory, "immutable");
        if (!Directory.GetFileSystemEntries(immutable).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .SequenceEqual(manifest.Artifacts.Select(a => a.RelativePath).Order(StringComparer.Ordinal))) return ProtectedStateStatus.ArtifactMismatch;
        foreach (var artifact in manifest.Artifacts)
        {
            var path = Path.Combine(immutable, artifact.RelativePath);
            if (!NoReparseAncestors(path)) return ProtectedStateStatus.UnsafePath;
            if (!_acl.VerifyFile(path)) return ProtectedStateStatus.AclRejected;
            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != artifact.Size || EvidenceIntegrity.HashBytes(bytes) != artifact.Hash) return ProtectedStateStatus.ArtifactMismatch;
        }
        foreach (var path in Directory.GetFileSystemEntries(Path.Combine(directory, "mutable")))
            if (!NoReparseAncestors(path) || Directory.Exists(path) || !_acl.VerifyFile(path)) return ProtectedStateStatus.AclRejected;
        return ProtectedStateStatus.Verified;
    }

    public string MutableDirectory(ProtectedStateAuthority authority)
    {
        if (Verify(authority).Status != ProtectedStateStatus.Verified) throw new IOException("Protected state is unavailable.");
        return Path.Combine(_root, authority.Binding.Endpoint.AgentId.ToString("N"), authority.Binding.ExecutionId.ToString("N"), "mutable");
    }

    private void EnsureDirectory(string path)
    {
        if (!NoReparseAncestors(Path.GetDirectoryName(path)!)) throw new IOException("Reparse ancestor rejected.");
        if (!Directory.Exists(path)) _acl.CreateProtected(path);
        if (!NoReparseAncestors(path) || !_acl.Verify(path)) throw new IOException("Protected ACL unavailable.");
    }

    private static bool NoReparseAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static bool SafeName(string name) => NamePattern().IsMatch(name) && !name.EndsWith('.') &&
        !ReservedPattern().IsMatch(name);
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedPattern();
}
