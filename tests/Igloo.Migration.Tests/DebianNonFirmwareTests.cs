using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianNonFirmwareTests
{
    private static DebianDeploymentPlanV1 WorkerPlan(TargetRootFixture.State state)
    {
        var plan = Plan(state);
        var files = plan.Agent.Payload.Files.Select(f => f.File.RelativePath == "\\igloo-agent\\agent.py"
            ? f with { File = f.File with { Length = DebianFirstBootEvidence.WorkerBytes().Length, Sha256 = DebianFirstBootEvidence.WorkerSha256 } } : f).ToImmutableArray();
        return plan with { Agent = plan.Agent with { Payload = plan.Agent.Payload with { Files = files },
            FirstBoot = new([new("DeploymentContent", "content.json", Hash), new("UserData", "user-data.json", new string('B', 64))]) } };
    }

    [Fact]
    public void DedicatedWorkerIsPinnedAndCannotBecomeLegacyAgent()
    {
        var state = TargetRootFixture.Create(); var plan = WorkerPlan(state);
        Assert.True(DebianAgentProfiles.VerifyWorkerPayload(plan).Value);
        Assert.Equal(ObservationAvailability.Unsupported, DebianAgentProfiles.VerifyWorkerPayload(Plan(state)).Availability);
        var changed = plan.Agent.Payload.Files.SetItem(0, plan.Agent.Payload.Files[0] with { File = plan.Agent.Payload.Files[0].File with { Sha256 = Hash } });
        Assert.Equal(ObservationAvailability.Ambiguous, DebianAgentProfiles.VerifyWorkerPayload(plan with { Agent = plan.Agent with { Payload = plan.Agent.Payload with { Files = changed } } }).Availability);
        Assert.Equal(ObservationAvailability.Unsupported, DebianAgentProfiles.WorkerQualification.Availability);
    }

    [Fact]
    public void InstalledWorkerEvidenceIsPendingWithExactHashesAndModes()
    {
        var plan = WorkerPlan(TargetRootFixture.Create()); var profile = DebianAgentProfiles.Declare(plan);
        var result = DebianFirstBootEvidence.Installed(profile, A(new DebianAgentReadbackV1(profile.GenerationId, profile.Files, profile.UnitContent, profile.EnableLink, profile.EnableTarget, false)));
        Assert.Equal(DebianFirstBootState.InstalledFirstBootPending, result.Value.State);
        Assert.Equal(DebianFirstBootEvidence.WorkerSha256, result.Value.WorkerSha256);
        Assert.Null(result.Value.FirstBootEvidenceSha256);
        Assert.DoesNotContain("Credential", profile.ConfigurationContent, StringComparison.Ordinal);
        Assert.Contains("PrivateDevices=yes", profile.UnitContent, StringComparison.Ordinal);
        Assert.Contains("ProtectHome=yes", profile.UnitContent, StringComparison.Ordinal);
        Assert.Contains("SystemCallFilter=~@mount @raw-io @reboot", profile.UnitContent, StringComparison.Ordinal);
        Assert.All(result.Value.InstalledFiles, f => { Assert.Equal(0u, f.Owner); Assert.Equal(0x1a4, f.Mode); });
    }

    [Theory]
    [InlineData("missing-content")]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-file")]
    [InlineData("path-escape")]
    [InlineData("unknown-action")]
    [InlineData("missing-hash")]
    public void InvalidFirstBootRequirementsRejected(string change)
    {
        var required = new DebianFirstBootPlanV1([new("DeploymentContent", "content.json", Hash)]);
        required = change switch
        {
            "missing-content" => new([new("UserData", "user-data.json", Hash)]),
            "duplicate-kind" => new([.. required.RequiredReceipts, new("DeploymentContent", "second.json", Hash)]),
            "duplicate-file" => new([.. required.RequiredReceipts, new("UserData", "content.json", Hash)]),
            "path-escape" => new([new("DeploymentContent", "../content.json", Hash)]),
            "unknown-action" => new([.. required.RequiredReceipts, new("FirmwareCleanup", "boot.json", Hash)]),
            "missing-hash" => new([new("DeploymentContent", "content.json", "")]),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(DebianFirstBootEvidence.Valid(required));
        Assert.Throws<InvalidDataException>(() => DebianFirstBootEvidence.Configuration(Id(111), required));
    }

    [Theory]
    [InlineData("FirstBootSucceeded")]
    [InlineData("FirstBootFailed")]
    public void FirstBootOutcomesRetainExactRequirements(string stateName)
    {
        var plan = WorkerPlan(TargetRootFixture.Create()); var profile = DebianAgentProfiles.Declare(plan);
        var installed = DebianFirstBootEvidence.Installed(profile, A(new DebianAgentReadbackV1(profile.GenerationId, profile.Files, profile.UnitContent, profile.EnableLink, profile.EnableTarget, false))).Value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, installed.GenerationId, installed.Profile,
            installed.WorkerSha256, installed.ConfigurationSha256, State = stateName, IntentSha256 = Hash, InvocationId = Id(990),
            VerifiedReceipts = stateName == "FirstBootSucceeded" ? plan.Agent.FirstBoot!.RequiredReceipts : [] });
        var result = DebianFirstBootEvidence.ObserveOutcome(installed, plan.Agent.FirstBoot!, A(bytes.ToImmutableArray()),
            A(new DebianFirstBootServiceV1(Id(990), "igloo-deployment.service", 1, stateName == "FirstBootSucceeded" ? 0 : 1,
                stateName == "FirstBootSucceeded" ? "success" : "exit-code")));
        Assert.Equal(stateName == "FirstBootSucceeded" ? DebianFirstBootState.FirstBootSucceeded : DebianFirstBootState.FirstBootFailed, result.Value.State);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.Value.FirstBootEvidenceSha256);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("config")]
    [InlineData("worker")]
    [InlineData("required")]
    [InlineData("intent")]
    [InlineData("state")]
    [InlineData("service")]
    [InlineData("invocation")]
    public void ChangedFirstBootEvidenceCannotSucceed(string change)
    {
        var plan = WorkerPlan(TargetRootFixture.Create()); var profile = DebianAgentProfiles.Declare(plan);
        var installed = DebianFirstBootEvidence.Installed(profile, A(new DebianAgentReadbackV1(profile.GenerationId, profile.Files, profile.UnitContent, profile.EnableLink, profile.EnableTarget, false))).Value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, GenerationId = change == "generation" ? Id(991) : installed.GenerationId,
            installed.Profile, WorkerSha256 = change == "worker" ? Hash : installed.WorkerSha256,
            ConfigurationSha256 = change == "config" ? Hash : installed.ConfigurationSha256,
            State = change == "state" ? "SuccessAnyway" : "FirstBootSucceeded", IntentSha256 = change == "intent" ? "" : Hash,
            InvocationId = change == "invocation" ? Id(998) : Id(990),
            VerifiedReceipts = change == "required" ? plan.Agent.FirstBoot!.RequiredReceipts.RemoveAt(1) : plan.Agent.FirstBoot!.RequiredReceipts });
        Assert.Equal(ObservationAvailability.Ambiguous, DebianFirstBootEvidence.ObserveOutcome(installed, plan.Agent.FirstBoot!, A(bytes.ToImmutableArray()),
            A(new DebianFirstBootServiceV1(Id(990), "igloo-deployment.service", 1, change == "service" ? 1 : 0,
                change == "service" ? "exit-code" : "success"))).Availability);
    }

    [Fact]
    public async Task AgentReceiptReopenRetainsPendingAndCannotCompleteMigration()
    {
        var state = TargetRootFixture.Create(); var plan = WorkerPlan(state); var runtime = new Runtime(state);
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        var profile = DebianAgentProfiles.Declare(plan);
        var installed = DebianFirstBootEvidence.Installed(profile, A(new DebianAgentReadbackV1(profile.GenerationId, profile.Files, profile.UnitContent, profile.EnableLink, profile.EnableTarget, false))).Value;
        var fixture = TargetRootFixture.Receipt(state);
        var receipt = DebianInstallationReceipts.Produce(run, [], fixture.Files, fixture.Firmware, installed);
        var bytes = DebianInstallationReceipts.Serialize(receipt);
        var reopened = DebianInstallationReceipts.Reopen(bytes, Convert.ToHexString(SHA256.HashData(bytes)), plan.Root.GenerationId, run.PlanSha256);
        Assert.Equal(DebianFirstBootState.InstalledFirstBootPending, reopened.Agent!.State);
        Assert.Equal(InstallationEvidenceState.Incomplete, DebianInstallationReceipts.Assess(reopened, A(state.Inventory), A(fixture.Files), A(fixture.Firmware!), A(ImmutableArray<DebianInstalledPackageV1>.Empty)).State);
        Assert.Throws<InvalidDataException>(() => DebianInstallationReceipts.Serialize(receipt with { Agent = installed with { GenerationId = Id(900) } }));
    }

    [Fact]
    public void FinalIdentityUsesEmptyFileAndRemovesTemporaryTransport()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var files = DebianNativeInstructions.Configuration(plan, DebianStage.FinalizeMachineIdentity, Context(state, plan, DebianStage.FinalizeMachineIdentity));
        Assert.Equal("", files.Single(f => f.Path == "/etc/machine-id").ContentOrTarget);
        Assert.Equal(DebianConfigurationKind.Utf8File, files.Single(f => f.Path == "/etc/machine-id").Kind);
        foreach (var path in new[] { "/usr/sbin/policy-rc.d", "/etc/apt/igloo-offline.sources", "/etc/igloo-installer-resolv.conf" })
            Assert.Equal(DebianConfigurationKind.MustBeAbsent, files.Single(f => f.Path == path).Kind);
        Assert.Contains(files, f => f.Path == "/etc/apt/sources.list.d/debian.sources");
    }

    [Fact]
    public void RuntimeIdentityIsIncludedInDurablePackageManifest()
    {
        var plan = Plan(TargetRootFixture.Create()); var packages = DebianContentFoundationTests.PackageSet(plan);
        var bytes = OfflineDebianPackageSets.Serialize(packages);
        Assert.Equal(Hash, OfflineDebianPackageSets.Reopen(bytes, Convert.ToHexString(SHA256.HashData(bytes)), plan.Root.GenerationId).RuntimeProfileSha256);
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Validate(packages with { RuntimeProfileSha256 = "bad" }, plan.Root.GenerationId).Availability);
    }

    [Fact]
    public void OrdinaryFileCannotBeAcceptedAsProtectedCredential()
    {
        var path = Path.Combine(Path.GetTempPath(), "igloo-credential-fixture-" + Guid.NewGuid().ToString("D"));
        try
        {
            using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite);
            if (OperatingSystem.IsLinux())
                Assert.Throws<IOException>(() => new DebianSealedCredentialInput(handle));
            else
                Assert.Throws<PlatformNotSupportedException>(() => new DebianSealedCredentialInput(handle));
        }
        finally { File.Delete(path); }
    }
}
