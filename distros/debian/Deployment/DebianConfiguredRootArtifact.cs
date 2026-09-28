using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

// Installation source evidence. This is deliberately not a RecoverySnapshot, a disk image,
// an authorization token, or an assertion that the installed system will boot.
public sealed record DebianRootBuildProfileV1(string Primitive, string Version, string ExecutableSha256,
    string HelperTreeSha256, string RuntimeProfileSha256, string IsolationProfile);
public sealed record DebianRootBuildAttestationV1(Guid BuildId, string PackageSetSha256,
    string PolicySha256, string BuilderProfileSha256, string ManifestSha256, string ContentSha256,
    string DpkgReadbackSha256, string DependencyReadbackSha256, string NeutralStateReadbackSha256,
    string IndependentVerifierSha256, DateTimeOffset VerifiedAtUtc)
{
    // Schema-1 history remains readable. A schema-2 real artifact must bind its
    // derived-root evidence; a clean package database alone is not neutrality.
    public DebianRootNeutralizationEvidenceV1? Neutralization { get; init; }
}
public sealed record DebianRootNeutralizationEvidenceV1(string PlanVersion, Guid DerivationId,
    Guid SourceBuildId, string SourceDiskSha256, string SourceObservationSha256,
    string SourcePackageSetFileSha256, string PlanSha256, string ResultSha256,
    string PostObservationSha256, string WholeTreeAuditSha256, string RegenerationContractSha256,
    bool SourcePreserved, bool ExactDeltaVerified, bool PackageStateVerified);
public sealed record DebianConfiguredRootArtifactV1(int SchemaVersion, Guid BuildId, string Release,
    string Architecture, string Format, DateTimeOffset CreatedAtUtc, DateTimeOffset SupportedUntilUtc,
    string MachineNeutralPolicy, OfflineDebianPackageSetV1 PackageSet, string PackageSetSha256,
    DebianRootBuildProfileV1 Builder, DebianOfflineFileV1 Content, DebianOfflineFileV1 Manifest,
    long LogicalBytes, ImmutableArray<DebianInstalledPackageV1> ConfiguredPackages,
    DebianRootBuildAttestationV1 Attestation);

// Obtained OUTSIDE the artifact. Development pins cannot certify a production release.
public sealed record DebianRootDevelopmentPinV1(Guid BuildId, string DescriptorSha256,
    string PolicySha256, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc);
public sealed record DebianRootAuthenticationV1(string Authority, string DescriptorSha256,
    DateTimeOffset SupportedUntilUtc);
public interface IDebianConfiguredRootAuthenticator
{
    Task<Observation<DebianRootAuthenticationV1>> AuthenticateAsync(ReadOnlyMemory<byte> descriptor,
        DateTimeOffset now, CancellationToken ct);
}

public static class DebianConfiguredRootArtifacts
{
    public const string Format = "igloo-semantic-root-stream-v1";
    public const int StreamHeaderLength = 23; // ASCII IGLOO-SEMANTIC-ROOT-V1 followed by LF.
    public const string NeutralPolicy = "debian-trixie-machine-neutral-v1";
    public const string FactoryIsolation = "disposable-vm-offline-no-passthrough-v1";
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static Observation<bool> ProductionAuthentication => Observations.Failure<bool>(
        ObservationAvailability.Unsupported, "DebianConfiguredRootReleaseAuthorityNotProvisioned");

    public static byte[] Serialize(DebianConfiguredRootArtifactV1 artifact)
    {
        RequireStructure(artifact);
        return JsonSerializer.SerializeToUtf8Bytes(artifact, Options);
    }

    // Integrity + externally supplied development trust only; never used by production dispatch.
    public static DebianConfiguredRootArtifactV1 ReopenDevelopmentPinned(ReadOnlySpan<byte> bytes,
        DebianRootDevelopmentPinV1 pin, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (bytes.Length is <= 0 or > 32 * 1024 * 1024 || !DebianDeploymentPlanning.Hash(pin.DescriptorSha256) ||
            Digest(bytes) != pin.DescriptorSha256 || now.Offset != TimeSpan.Zero ||
            pin.NotBeforeUtc.Offset != TimeSpan.Zero || pin.NotAfterUtc.Offset != TimeSpan.Zero ||
            now < pin.NotBeforeUtc || now >= pin.NotAfterUtc)
            throw new InvalidDataException("Configured-root development trust or integrity rejected.");
        RejectDuplicateProperties(bytes);
        var artifact = JsonSerializer.Deserialize<DebianConfiguredRootArtifactV1>(bytes, Options) ??
            throw new InvalidDataException("Missing configured-root artifact.");
        RequireStructure(artifact);
        if (artifact.BuildId != pin.BuildId || artifact.PackageSet.PolicySha256 != pin.PolicySha256 ||
            now < artifact.CreatedAtUtc || now >= artifact.SupportedUntilUtc)
            throw new InvalidDataException("Configured-root build, policy or support window changed.");
        return artifact;
    }

    public static void RequireStructure(DebianConfiguredRootArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var source = artifact.PackageSet;
        if (source is null || artifact.Builder is null || artifact.Attestation is null ||
            artifact.SchemaVersion is not (1 or 2) || artifact.BuildId == Guid.Empty || artifact.BuildId != source.BuildId ||
            artifact.Release != "trixie" || artifact.Architecture != "amd64" || artifact.Format != Format ||
            artifact.MachineNeutralPolicy != NeutralPolicy || artifact.CreatedAtUtc.Offset != TimeSpan.Zero ||
            artifact.CreatedAtUtc == default || artifact.SupportedUntilUtc.Offset != TimeSpan.Zero ||
            artifact.SupportedUntilUtc <= artifact.CreatedAtUtc ||
            artifact.SupportedUntilUtc - artifact.CreatedAtUtc > TimeSpan.FromDays(30) ||
            OfflineDebianPackageSets.Validate(source, source.GenerationId).Availability != ObservationAvailability.Available ||
            Digest(OfflineDebianPackageSets.Serialize(source)) != artifact.PackageSetSha256 ||
            artifact.Content is null || artifact.Manifest is null ||
            artifact.Content.Path != "root.content" || artifact.Manifest.Path != "root.manifest.json" ||
            !File(artifact.Content, 64L * 1024 * 1024 * 1024) || !File(artifact.Manifest, 128 * 1024 * 1024) ||
            artifact.LogicalBytes < 0 || artifact.LogicalBytes != artifact.Content.Length - StreamHeaderLength ||
            artifact.ConfiguredPackages.IsDefaultOrEmpty ||
            artifact.ConfiguredPackages.Any(p => p is null || p.DpkgStatus != "install ok installed") ||
            !artifact.ConfiguredPackages.OrderBy(p => p.Name, StringComparer.Ordinal).SequenceEqual(
                source.Packages.Select(p => new DebianInstalledPackageV1(p.Name, p.Version, p.Architecture, "install ok installed"))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Incomplete configured-root source, package state or content binding.");
        var builder = artifact.Builder;
        if (!source.DependencyPolicy.AppliedExceptions.IsEmpty &&
            (artifact.CreatedAtUtc < DebianRecommendationPolicy.Wsdd.ReviewedUtc ||
             artifact.SupportedUntilUtc > DebianRecommendationPolicy.Wsdd.ReviewBeforeUtc))
            throw new InvalidDataException("Artifact support extends beyond dependency-policy review.");
        if (builder.Primitive != "mmdebstrap" || builder.Version != "1.5.7-1+deb13u1" ||
            builder.IsolationProfile != FactoryIsolation ||
            !Hashes(builder.ExecutableSha256, builder.HelperTreeSha256, builder.RuntimeProfileSha256))
            throw new InvalidDataException("Unqualified configured-root factory profile.");
        var evidence = artifact.Attestation;
        var neutral = evidence.Neutralization;
        if ((artifact.SchemaVersion == 2 && neutral is null) || (neutral is not null &&
            (neutral.PlanVersion is not ("debian-trixie-neutralization-2026-09-28-v1" or "debian-trixie-neutralization-2026-09-28-v2") || neutral.DerivationId == Guid.Empty ||
             neutral.SourceBuildId != artifact.BuildId || !neutral.SourcePreserved || !neutral.ExactDeltaVerified ||
             !neutral.PackageStateVerified || !Hashes(neutral.SourceDiskSha256, neutral.SourceObservationSha256,
                 neutral.SourcePackageSetFileSha256, neutral.PlanSha256, neutral.ResultSha256,
                 neutral.PostObservationSha256, neutral.WholeTreeAuditSha256, neutral.RegenerationContractSha256))))
            throw new InvalidDataException("Configured-root neutralization derivation is incomplete or substituted.");
        if (evidence.BuildId != artifact.BuildId || evidence.PackageSetSha256 != artifact.PackageSetSha256 ||
            evidence.PolicySha256 != source.PolicySha256 ||
            evidence.BuilderProfileSha256 != Digest(JsonSerializer.SerializeToUtf8Bytes(builder, Options)) ||
            evidence.ManifestSha256 != artifact.Manifest.Sha256 || evidence.ContentSha256 != artifact.Content.Sha256 ||
            !Hashes(evidence.DpkgReadbackSha256, evidence.DependencyReadbackSha256,
                evidence.NeutralStateReadbackSha256, evidence.IndependentVerifierSha256) ||
            evidence.VerifiedAtUtc.Offset != TimeSpan.Zero || evidence.VerifiedAtUtc < artifact.CreatedAtUtc ||
            evidence.VerifiedAtUtc >= artifact.SupportedUntilUtc)
            throw new InvalidDataException("Configured-root build evidence is incomplete or substituted.");
        // Structure cannot certify that these retained readbacks were performed. Publication
        // requires an independent factory verifier and an external release authority.
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Hashes(params string[] values) => values.All(DebianDeploymentPlanning.Hash);
    private static bool File(DebianOfflineFileV1 file, long limit) => file.Length > 0 && file.Length <= limit && DebianDeploymentPlanning.Hash(file.Sha256);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate artifact property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }
}
