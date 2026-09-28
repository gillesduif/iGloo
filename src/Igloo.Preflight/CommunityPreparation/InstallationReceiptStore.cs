using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Preparation;
using Igloo.Preflight.CommunityRecovery;

namespace Igloo.Preflight.CommunityPreparation;

public sealed record InstallationReceiptReference(Guid ArtifactId, Guid GenerationId, string Sha256);

// Existing Community artifact storage provides create-new, flush and fresh-handle reopen.
// Reopen establishes byte integrity and structure only. Fresh target/file/firmware inspection
// and a supported distro producer remain separate; this store cannot authorize completion.
public sealed class InstallationReceiptStore(ICommunityRecoveryArtifactStore artifacts)
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public InstallationReceiptReference PersistAndReopen(InstallationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireStructure(receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        var reference = new InstallationReceiptReference(Guid.NewGuid(), receipt.Root.GenerationId, Convert.ToHexString(SHA256.HashData(bytes)));
        artifacts.PersistNew(reference.ArtifactId, bytes);
        _ = Reopen(reference);
        return reference;
    }

    public InstallationReceiptV1 Reopen(InstallationReceiptReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var bytes = artifacts.Reopen(reference.ArtifactId);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reference.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Installation receipt integrity mismatch.");
        var receipt = JsonSerializer.Deserialize<InstallationReceiptV1>(bytes, Options)
            ?? throw new InvalidDataException("Missing installation receipt.");
        RequireStructure(receipt);
        if (receipt.Root.GenerationId != reference.GenerationId) throw new InvalidDataException("Installation receipt generation changed.");
        return receipt;
    }

    private static void RequireStructure(InstallationReceiptV1 receipt)
    {
        if (!InstallationReceiptRules.IsStructurallyValid(receipt)) throw new InvalidDataException("Invalid installation receipt structure.");
    }
}
