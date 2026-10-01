using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// A declaration, not a capability: the factory below also requires independently
// validated storage and the external pin. No Windows ownership or boot facts exist here.
public sealed record DebianLabImportPlanV1(int SchemaVersion, InstallationStorageProvenanceV1 Provenance,
    Guid BuildId, Guid DerivationId, string DescriptorSha256, string PolicySha256,
    string ManifestSha256, string ContentSha256, long ContentLength, string Transport,
    string? TransportManifestSha256, long OtherPayloadBytes) : IDebianImportSourceBinding
{
    [JsonIgnore]
    public Observation<bool> TransportSupport => (Transport == "SingleFile" && TransportManifestSha256 is null && ContentLength is > 0 and <= uint.MaxValue) ||
        (Transport == "Chunked" && DebianDeploymentPlanning.Hash(TransportManifestSha256) && ContentLength is > 0 and <= DebianRootTransports.MaximumContentLength)
        ? Observations.Available(true)
        : Observations.Failure<bool>(ObservationAvailability.Unsupported, "ConfiguredRootTransportInvalid");

    public void VerifyArtifact(DebianConfiguredRootArtifactV1 artifact, ReadOnlySpan<byte> descriptor)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        DebianImportSourceBinding.VerifyArtifact(this, artifact, descriptor);
    }

    internal string Fingerprint(ValidatedInstallationStorage storage, DebianRootDevelopmentPinV1 pin)
    {
        if (SchemaVersion != 1 || Provenance != storage.Provenance || Provenance.Provider != "IsolatedFileBackedLab" ||
            Provenance.Version != 2 || Provenance.Scope != "ConfiguredRootImport" ||
            BuildId == Guid.Empty || DerivationId == Guid.Empty || OtherPayloadBytes < 0 || OtherPayloadBytes > DebianRootTransports.MaximumContentLength ||
            TransportSupport.Availability != ObservationAvailability.Available ||
            new[] { DescriptorSha256, PolicySha256, ManifestSha256, ContentSha256 }.Any(h => !DebianDeploymentPlanning.Hash(h)) ||
            pin.BuildId != BuildId || pin.DescriptorSha256 != DescriptorSha256 || pin.PolicySha256 != PolicySha256 ||
            pin.NotAfterUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Lab import storage/source authorization mismatch.");
        // Pin projection binds its validity and policy too. Artifact bytes remain reusable;
        // this operation fingerprint binds the fresh target and external trust decision.
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { Plan = this, ExternalPin = pin }));
    }
}
