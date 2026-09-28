using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// Import is a separate phase: no invented user, credential, agent or installation-complete state.
public sealed record DebianConfiguredRootImportPlanV1(InstallationOwnershipV1 Ownership,
    RootFileSystemReceiptV1 Root, Guid BuildId, Guid DerivationId, string DescriptorSha256,
    string PolicySha256, string ManifestSha256, string ContentSha256, long ContentLength)
{
    public string Transport { get; init; } = "SingleFile";
    public string? TransportManifestSha256 { get; init; }
    // Other mandatory payload bytes are explicitly budgeted in the immutable plan.
    public long OtherPayloadBytes { get; init; }
    public string SourceDirectory => $"configured-root/{BuildId:D}/{DerivationId:D}";

    public string Fingerprint()
    {
        if (Ownership is null || Root is null || Root.GenerationId == Guid.Empty ||
            Root.GenerationId != Ownership.Layout.Plan.GenerationId || Root.FileSystem != "EXT4" || Root.FileSystemUuid == Guid.Empty ||
            BuildId == Guid.Empty || DerivationId == Guid.Empty || ContentLength <= 0 || ContentLength > DebianRootTransports.MaximumContentLength ||
            OtherPayloadBytes < 0 || OtherPayloadBytes > DebianRootTransports.MaximumContentLength ||
            Transport is not ("SingleFile" or "Chunked") ||
            (Transport == "SingleFile" ? TransportManifestSha256 is not null : !DebianDeploymentPlanning.Hash(TransportManifestSha256)) ||
            new[] { DescriptorSha256, PolicySha256, ManifestSha256, ContentSha256 }.Any(h => !DebianDeploymentPlanning.Hash(h)) ||
            PreparedStorageOwnership.VerifyStructure(Ownership.Layout).Availability != ObservationAvailability.Available ||
            !Ownership.Layout.StorageOwnership!.CreatedPartitions.Any(p => p.Role == PreparationRole.LinuxRoot && p.Identity == Root.Partition))
            throw new InvalidDataException("Invalid configured-root import binding.");
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(this));
    }

    // Limit physical transport objects; a chunked logical stream is not one FAT32 file.
    [JsonIgnore]
    public Observation<bool> TransportSupport => (Transport == "SingleFile" && TransportManifestSha256 is null && ContentLength is > 0 and <= uint.MaxValue) ||
        (Transport == "Chunked" && DebianDeploymentPlanning.Hash(TransportManifestSha256) && ContentLength is > 0 and <= DebianRootTransports.MaximumContentLength)
        ? Observations.Available(true)
        : Observations.Failure<bool>(ObservationAvailability.Unsupported, "ConfiguredRootContentExceedsFat32SingleFile");

    internal void VerifyArtifact(DebianConfiguredRootArtifactV1 artifact, ReadOnlySpan<byte> descriptor)
    {
        DebianConfiguredRootArtifacts.RequireStructure(artifact);
        if (artifact.SchemaVersion != 2 || artifact.BuildId != BuildId || artifact.Attestation.Neutralization?.DerivationId != DerivationId ||
            artifact.Attestation.Neutralization.PlanVersion != "debian-trixie-neutralization-2026-09-28-v2" ||
            DebianConfiguredRootArtifacts.Digest(descriptor) != DescriptorSha256 || artifact.PackageSet.PolicySha256 != PolicySha256 ||
            artifact.Manifest.Sha256 != ManifestSha256 || artifact.Content.Sha256 != ContentSha256 || artifact.Content.Length != ContentLength)
            throw new InvalidDataException("Import source differs from immutable plan.");
        if (TransportSupport.Availability != ObservationAvailability.Available) throw new NotSupportedException(TransportSupport.Code);
    }
}

// Explicit development composition only. Production has no default authenticator and remains
// Unsupported. A pin is an out-of-band source trust decision, never target authorization.
public sealed class DebianDevelopmentRootAuthenticator(DebianRootDevelopmentPinV1 pin) : IDebianConfiguredRootAuthenticator
{
    public Task<Observation<DebianRootAuthenticationV1>> AuthenticateAsync(ReadOnlyMemory<byte> descriptor, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var artifact = DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(descriptor.Span, pin, now);
        return Task.FromResult(Observations.Available(new DebianRootAuthenticationV1("DevelopmentImportOnly",
            pin.DescriptorSha256, artifact.SupportedUntilUtc < pin.NotAfterUtc ? artifact.SupportedUntilUtc : pin.NotAfterUtc)));
    }
}
