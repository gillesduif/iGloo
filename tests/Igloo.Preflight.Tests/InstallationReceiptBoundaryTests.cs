using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Preflight.Tests;

public sealed class InstallationReceiptBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igloo-install-receipt-test-" + Guid.NewGuid().ToString("D"));

    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    public void ReopenedCompleteEvidenceStillDoesNotEnableProduction(string distro)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state, distro);
        var reference = Store().PersistAndReopen(receipt);
        var reopened = Store().Reopen(reference);
        Assert.Equal(receipt.Root, reopened.Root);
        Assert.Equal(InstallationEvidenceState.EvidenceComplete, InstallationReceiptRules.AssessEvidence(reopened,
            A(state.Inventory), A(receipt.Files), A(receipt.Firmware!)).State);
        Assert.Equal(ObservationAvailability.Unsupported, DedicatedEspPreparationSupport.Production.Availability);
    }

    [Fact]
    public void ReopeningPartialReceiptNeverPromotesCompletion()
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state) with { Stages = [], Files = [], Firmware = null };
        var reference = Store().PersistAndReopen(receipt);
        var result = InstallationReceiptRules.AssessEvidence(Store().Reopen(reference), A(state.Inventory),
            Observations.Failure<ImmutableArray<InstalledFileEvidenceV1>>(ObservationAvailability.Unavailable, "not-read"),
            Observations.Failure<InstalledFirmwareEvidenceV1>(ObservationAvailability.Unavailable, "not-read"));
        Assert.Equal(InstallationEvidenceState.Incomplete, result.State);
    }

    [Fact]
    public void ChangedGenerationOrCorruptionPreventsReopen()
    {
        var receipt = TargetRootFixture.Receipt(TargetRootFixture.Create());
        var reference = Store().PersistAndReopen(receipt);
        Assert.Throws<InvalidDataException>(() => Store().Reopen(reference with { GenerationId = Id(900) }));
        File.AppendAllText(Path.Combine(_root, reference.ArtifactId.ToString("D") + ".recovery.json"), " ");
        Assert.Throws<InvalidDataException>(() => Store().Reopen(reference));
    }

    [Fact]
    public void ReopenFailureRemainsFailure()
    {
        var receipt = TargetRootFixture.Receipt(TargetRootFixture.Create());
        Assert.Throws<IOException>(() => new InstallationReceiptStore(new Unreadable()).PersistAndReopen(receipt));
    }

    private InstallationReceiptStore Store() => new(new CommunityRecoveryArtifactStore(_root));
    private sealed class Unreadable : ICommunityRecoveryArtifactStore
    {
        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact) { }
        public byte[] Reopen(Guid artifactId) => throw new IOException("Independent read failed.");
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
