using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianConfiguredRootTests
{
    internal static DebianConfiguredRootArtifactV1 Artifact()
    {
        var set = DebianContentFoundationTests.PackageSet(Plan(TargetRootFixture.Create()));
        var sourceHash = DebianConfiguredRootArtifacts.Digest(OfflineDebianPackageSets.Serialize(set));
        var builder = new DebianRootBuildProfileV1("mmdebstrap", "1.5.7-1+deb13u1", Hash, Hash, Hash,
            DebianConfiguredRootArtifacts.FactoryIsolation);
        var attestation = new DebianRootBuildAttestationV1(set.BuildId, sourceHash, set.PolicySha256,
            DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(builder)), Hash, Hash,
            Hash, Hash, Hash, Hash, Now);
        return new(1, set.BuildId, "trixie", "amd64", DebianConfiguredRootArtifacts.Format, Now, Now.AddDays(7),
            DebianConfiguredRootArtifacts.NeutralPolicy, set, sourceHash, builder,
            new("root.content", 102, Hash), new("root.manifest.json", 200, Hash), 79,
            set.Packages.Select(p => new DebianInstalledPackageV1(p.Name, p.Version, p.Architecture, "install ok installed")).ToImmutableArray(), attestation);
    }

    private static DebianRootDevelopmentPinV1 Pin(DebianConfiguredRootArtifactV1 artifact, byte[] bytes) =>
        new(artifact.BuildId, DebianConfiguredRootArtifacts.Digest(bytes), artifact.PackageSet.PolicySha256, Now, Now.AddDays(7));

    [Fact]
    public void ArtifactReopenIsDevelopmentOnlyAndDeterministic()
    {
        var artifact = Artifact();
        var bytes = DebianConfiguredRootArtifacts.Serialize(artifact);
        var reopened = DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(bytes.ToArray(), Pin(artifact, bytes), Now);
        Assert.Equal(bytes, DebianConfiguredRootArtifacts.Serialize(reopened));
        Assert.Equal(ObservationAvailability.Unsupported, DebianConfiguredRootArtifacts.ProductionAuthentication.Availability);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
        Assert.DoesNotContain("Password", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("architecture")]
    [InlineData("generation")]
    [InlineData("source")]
    [InlineData("builder")]
    [InlineData("format")]
    [InlineData("manifest")]
    [InlineData("artifact")]
    [InlineData("attestation")]
    [InlineData("package-version")]
    [InlineData("unconfigured")]
    [InlineData("pending-trigger")]
    [InlineData("missing-package")]
    [InlineData("machine-policy")]
    [InlineData("expired-window")]
    [InlineData("unbounded-window")]
    [InlineData("logical-size")]
    public void IncompleteOrChangedArtifactRejected(string change)
    {
        var value = Artifact();
        value = change switch
        {
            "release" => value with { Release = "forky" },
            "architecture" => value with { Architecture = "arm64" },
            "generation" => value with { BuildId = Guid.NewGuid() },
            "source" => value with { PackageSetSha256 = Hash },
            "builder" => value with { Builder = value.Builder with { IsolationProfile = "host-chroot" } },
            "format" => value with { Format = "tar-extract" },
            "manifest" => value with { Manifest = value.Manifest with { Sha256 = "B" + Hash[1..] } },
            "artifact" => value with { Content = value.Content with { Path = "../root.content" } },
            "attestation" => value with { Attestation = value.Attestation with { IndependentVerifierSha256 = "missing" } },
            "package-version" => value with { ConfiguredPackages = value.ConfiguredPackages.SetItem(0, value.ConfiguredPackages[0] with { Version = "2.changed" }) },
            "unconfigured" => value with { ConfiguredPackages = value.ConfiguredPackages.SetItem(0, value.ConfiguredPackages[0] with { DpkgStatus = "install ok unpacked" }) },
            "pending-trigger" => value with { ConfiguredPackages = value.ConfiguredPackages.SetItem(0, value.ConfiguredPackages[0] with { DpkgStatus = "install ok triggers-pending" }) },
            "missing-package" => value with { ConfiguredPackages = value.ConfiguredPackages.RemoveAt(0) },
            "machine-policy" => value with { MachineNeutralPolicy = "builder-identity-retained" },
            "expired-window" => value with { SupportedUntilUtc = Now },
            "unbounded-window" => value with { SupportedUntilUtc = Now.AddYears(1) },
            "logical-size" => value with { LogicalBytes = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Throws<InvalidDataException>(() => DebianConfiguredRootArtifacts.Serialize(value));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("build")]
    [InlineData("policy")]
    [InlineData("expired")]
    [InlineData("future")]
    public void EmbeddedClaimsCannotReplaceExternalPin(string change)
    {
        var value = Artifact(); var bytes = DebianConfiguredRootArtifacts.Serialize(value); var pin = Pin(value, bytes);
        pin = change switch
        {
            "hash" => pin with { DescriptorSha256 = Hash },
            "build" => pin with { BuildId = Guid.NewGuid() },
            "policy" => pin with { PolicySha256 = Hash },
            "expired" => pin with { NotAfterUtc = Now },
            "future" => pin with { NotBeforeUtc = Now.AddDays(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Throws<InvalidDataException>(() => DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(bytes, pin, Now));
    }

    [Fact]
    public void CorruptAndDuplicatePropertiesRejected()
    {
        var value = Artifact(); var bytes = DebianConfiguredRootArtifacts.Serialize(value); var pin = Pin(value, bytes);
        bytes[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(bytes, pin, Now));
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DebianConfiguredRootArtifacts.Serialize(value))
            .Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(duplicate, Pin(value, duplicate), Now));
    }

    [Fact]
    public void ExistingStageReceiptHistoryIsUnchanged()
    {
        Assert.Equal(43, DebianDeploymentStages.Ordered.Length);
        Assert.Equal(9, (int)DebianStage.Bootstrap);
        Assert.Equal(DebianConfiguredRootResponsibility.ImportConfiguredRoot, DebianConfiguredRootStages.Responsibility(DebianStage.Bootstrap));
        Assert.Equal(5, DebianDeploymentStages.Ordered.Count(s => DebianConfiguredRootStages.Responsibility(s) == DebianConfiguredRootResponsibility.ArtifactReadback));
        Assert.Equal(9, DebianDeploymentStages.Ordered.Count(s => DebianConfiguredRootStages.Responsibility(s) == DebianConfiguredRootResponsibility.FirmwareDeferred));
        Assert.Equal(28, DebianDeploymentStages.Ordered.Count(s => DebianConfiguredRootStages.Responsibility(s) == DebianConfiguredRootResponsibility.TargetSpecific));
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(Plan(TargetRootFixture.Create()), DebianStage.Bootstrap));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("valid-v2")]
    [InlineData("missing")]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("unknown-delta")]
    [InlineData("unverified-packages")]
    [InlineData("source-mutated")]
    [InlineData("audit")]
    public void RealArtifactRequiresBoundNeutralizationEvidence(string change)
    {
        var artifact = Artifact() with { SchemaVersion = 2 };
        var evidence = new DebianRootNeutralizationEvidenceV1("debian-trixie-neutralization-2026-09-28-v1",
            Guid.NewGuid(), artifact.BuildId, Hash, Hash, Hash, Hash, Hash, Hash, Hash, Hash, true, true, true);
        evidence = change switch
        {
            "valid-v2" => evidence with { PlanVersion = "debian-trixie-neutralization-2026-09-28-v2" },
            "source" => evidence with { SourceBuildId = Guid.NewGuid() },
            "plan" => evidence with { PlanVersion = "generic-cleanup" },
            "unknown-delta" => evidence with { ExactDeltaVerified = false },
            "unverified-packages" => evidence with { PackageStateVerified = false },
            "source-mutated" => evidence with { SourcePreserved = false },
            "audit" => evidence with { WholeTreeAuditSha256 = "unavailable" },
            _ => evidence,
        };
        artifact = artifact with { Attestation = artifact.Attestation with { Neutralization = change == "missing" ? null : evidence } };
        if (change is "valid" or "valid-v2")
        {
            var bytes = DebianConfiguredRootArtifacts.Serialize(artifact);
            Assert.Equal(bytes, DebianConfiguredRootArtifacts.Serialize(DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(bytes, Pin(artifact, bytes), Now)));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => DebianConfiguredRootArtifacts.Serialize(artifact));
        }
    }

    [Fact]
    public async Task CanonicalLeaseCannotBypassUnqualifiedProductionImporter()
    {
        var fixture = TargetRootFixture.Create();
        var leases = InstallerBlockLeases.Acquire(fixture.Ownership, fixture.Root, fixture.Root.GenerationId,
            Guid.NewGuid(), Now, Observations.Available(fixture.Inventory), Observations.Available(fixture.Mounts.DeviceNumbers));
        Assert.Equal(ObservationAvailability.Available, leases.Availability);
        await using var session = new DebianNativeMountSession(new(Plan(fixture), new Journal()));
        Assert.Equal(ObservationAvailability.Unsupported, DebianConfiguredRootStages.ImportSupport(session, leases.Value).Availability);
    }
}
