using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Core.Tests;

public sealed class InstallationReceiptTests
{
    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    public void CompleteSyntheticEvidenceDoesNotCertifyADistroEngine(string distro)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state, distro);
        var result = Assess(state, receipt);
        Assert.Equal(InstallationEvidenceState.EvidenceComplete, result.State);
        Assert.Equal("InstallationEvidenceCompleteNotBootabilityOrAuthorization", result.Code);
        Assert.Equal("unqualified-fixture-v1", receipt.StrategyVersion);
    }

    [Theory]
    [InlineData(DeploymentStage.FilesystemDeployed)]
    [InlineData(DeploymentStage.AptConfigured)]
    [InlineData(DeploymentStage.KernelInstalled)]
    [InlineData(DeploymentStage.InitramfsGenerated)]
    [InlineData(DeploymentStage.SystemIdentityFinalized)]
    [InlineData(DeploymentStage.SignedBootPackagesConfigured)]
    [InlineData(DeploymentStage.BootloaderFilesFinalized)]
    [InlineData(DeploymentStage.FirmwareFinalized)]
    [InlineData(DeploymentStage.AgentInstalled)]
    [InlineData(DeploymentStage.TargetUnmounted)]
    public void FailureOrUncertainOutcomeAtEveryCriticalStagePreventsCompleteEvidence(DeploymentStage stage)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state);
        var index = Array.FindIndex(receipt.Stages.ToArray(), s => s.Stage == stage);
        var failed = receipt.Stages[index] with { Outcome = DeploymentStageOutcome.Failed, ToolExitCode = 1, ReadbackSha256 = null };
        Assert.Equal(InstallationEvidenceState.Failed, Assess(state, receipt with { Stages = receipt.Stages.SetItem(index, failed) }).State);
        var unknown = failed with { Outcome = DeploymentStageOutcome.OutcomeUnknown, ToolExitCode = null };
        Assert.Equal(InstallationEvidenceState.OutcomeUnknown, Assess(state, receipt with { Stages = receipt.Stages.SetItem(index, unknown) }).State);
    }

    [Fact]
    public void AnyMissingStageKeepsReceiptIncomplete()
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state);
        foreach (var stage in receipt.Stages)
            Assert.Equal(InstallationEvidenceState.Incomplete, Assess(state, receipt with { Stages = receipt.Stages.Remove(stage) }).State);
    }

    [Theory]
    [InlineData("windows-loader")]
    [InlineData("wrong-root")]
    [InlineData("missing-file")]
    [InlineData("changed-hash")]
    [InlineData("different-kernel")]
    [InlineData("different-efi-directory")]
    [InlineData("source-hash")]
    [InlineData("generation")]
    public void IncompleteOrMisboundContentCannotComplete(string change)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state);
        var files = receipt.Files; var fresh = files;
        var shim = Array.FindIndex(files.ToArray(), f => f.Role == InstalledFileRole.Shim);
        var initramfs = Array.FindIndex(files.ToArray(), f => f.Role == InstalledFileRole.Initramfs);
        switch (change)
        {
            case "windows-loader": files = files.SetItem(shim, files[shim] with { PartitionGuid = state.Ownership.Esp.WindowsEsp.Volume.PartitionGuid }); break;
            case "wrong-root": files = files.SetItem(0, files[0] with { PartitionGuid = Id(900) }); break;
            case "missing-file": files = files.RemoveAt(0); break;
            case "changed-hash": fresh = files.SetItem(0, files[0] with { Sha256 = new string('F', 64) }); break;
            case "different-kernel": files = files.SetItem(initramfs, files[initramfs] with { Path = "/boot/initrd.img-other" }); fresh = files; break;
            case "different-efi-directory": files = files.SetItem(shim, files[shim] with { Path = "/EFI/other/shimx64.efi" }); fresh = files; break;
            case "source-hash": receipt = receipt with { Source = receipt.Source with { Sha256 = "unavailable" } }; break;
            case "generation": receipt = receipt with { Root = receipt.Root with { GenerationId = Id(900) } }; break;
        }
        receipt = receipt with { Files = files };
        Assert.NotEqual(InstallationEvidenceState.EvidenceComplete,
            InstallationReceiptRules.AssessEvidence(receipt, A(state.Inventory), A(fresh), A(receipt.Firmware!)).State);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void IndependentReadbackCannotBeDefaulted(ObservationAvailability availability)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state);
        var result = InstallationReceiptRules.AssessEvidence(receipt, A(state.Inventory),
            Observations.Failure<ImmutableArray<InstalledFileEvidenceV1>>(availability, "probe"), A(receipt.Firmware!));
        Assert.Equal(availability, result.Availability);
        result = InstallationReceiptRules.AssessEvidence(receipt, A(state.Inventory), A(receipt.Files),
            Observations.Failure<InstalledFirmwareEvidenceV1>(availability, "probe"));
        Assert.Equal(availability, result.Availability);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("missing-order")]
    [InlineData("attributes")]
    [InlineData("malformed")]
    public void FirmwareWitnessMustCorrelateWithoutGuessing(string change)
    {
        var state = TargetRootFixture.Create(); var receipt = TargetRootFixture.Receipt(state); var firmware = receipt.Firmware!;
        firmware = change switch
        {
            "opaque" => firmware with { EntryBytes = firmware.EntryBytes.Add(1) },
            "missing-order" => firmware with { OrderBytes = [0, 0] },
            "attributes" => firmware with { EntryNativeAttributes = 0 },
            _ => firmware with { EntryBytes = [1] },
        };
        Assert.NotEqual(InstallationEvidenceState.EvidenceComplete, Assess(state, receipt with { Firmware = firmware }).State);
    }

    private static InstallationReceiptAssessment Assess(TargetRootFixture.State state, InstallationReceiptV1 receipt) =>
        InstallationReceiptRules.AssessEvidence(receipt, A(state.Inventory), A(receipt.Files), A(receipt.Firmware!));
}
