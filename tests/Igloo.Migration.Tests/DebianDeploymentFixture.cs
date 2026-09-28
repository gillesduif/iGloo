using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

internal static class DebianDeploymentFixture
{
    internal static string Hash => new('A', 64);
    internal static DateTimeOffset Now => new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    internal static DebianDeploymentPlanV1 Plan(TargetRootFixture.State state)
    {
        var packages = DebianDeploymentPlanning.WorkstationPackages.Append("linux-image-6.fixture")
            .Select(p => new DebianPackageV1(p, "1.fixture", "amd64", 99, Hash)).ToImmutableArray();
        var source = new DebianSourceV1("trixie", "amd64", "debian-bundle", Hash, Hash, Hash,
            new[] { "trixie", "trixie-updates", "trixie-security" }.Select(s => new DebianArchiveV1(s,
                new Uri("https://deb.debian.org/debian"), Hash, Hash, new string('B', 40), Now.AddDays(7))).ToImmutableArray(), packages,
            new("debian-source-manifest.json", 99, Hash, Hash));
        var payload = state.Ownership.Esp.Payload.Volume;
        var agent = new DebianAgentV1(new(state.Root.GenerationId,
            new[] { "\\igloo-agent\\agent.py", "\\migration-manifest.json", "\\igloo-agent\\igloo-deployment.service" }
                .Select(p => new PreparedInstallerPayloadFileV1(PreparationRole.Payload,
                    new CanonicalFileIdentityV1(payload.VolumeGuid, p, 99, Hash))).ToImmutableArray()), "igloo-agent/igloo-deployment.service");
        return new(1, state.Ownership, state.Root, source, new("debian-workstation", "gilles", "en_US.UTF-8", "Europe/Brussels", "be", Hash), agent);
    }

    internal static DebianRuntimeContextV1 Context(TargetRootFixture.State state, DebianDeploymentPlanV1 plan, DebianStage stage)
    {
        var raw = state.Mounts;
        if (!DebianDeploymentStages.RequiresCompleteMounts(stage) && stage >= DebianStage.MountRoot)
        {
            var keepRoot = stage is not (DebianStage.MountRoot or DebianStage.PersistCompletionEvidence);
            var keepEsp = stage is DebianStage.MountPayload or DebianStage.UnmountLinuxEsp;
            var keepPayload = stage is DebianStage.UnmountLinuxEsp or DebianStage.UnmountPayload;
            raw = raw with { Mounts = raw.Mounts.Where(m => m.MountPoint != state.MountPlan.Root || keepRoot)
                .Where(m => m.MountPoint != state.MountPlan.Esp || keepEsp)
                .Where(m => m.MountPoint != state.MountPlan.Payload || keepPayload).ToImmutableArray() };
        }
        if (DebianDeploymentStages.RequiresHelpers(stage))
        {
            var mounts = raw.Mounts.ToBuilder(); var paths = raw.Paths.ToBuilder();
            foreach (var helper in DebianTargetBoundary.Helpers)
            {
                var path = state.MountPlan.Root + helper.RelativePath;
                var parent = mounts.Single(m => m.MountPoint == state.MountPlan.Root + helper.Parent).MountId;
                var payload = mounts.Single(m => m.MountPoint == state.MountPlan.Payload);
                var id = (uint)(20 + mounts.Count);
                mounts.Add(new(id, parent, helper.RelativePath == "/run/igloo-source" ? payload.Major : 0,
                    helper.RelativePath == "/run/igloo-source" ? payload.Minor : id,
                    "/", path, helper.FileSystem, [helper.Mode], [], ["rw"]));
                paths.Add(new(path, path, true, false));
            }
            raw = raw with { Mounts = mounts.ToImmutable(), Paths = paths.ToImmutable() };
        }
        return new(plan.Root.GenerationId, A(state.Inventory), A(raw), A(new DebianIsolationReadbackV1(plan.Root.GenerationId,
            DebianDeploymentPlanning.Fingerprint(plan), Enum.GetValues<DebianIsolationRequirement>().ToImmutableArray(), Hash)));
    }

    internal static DebianStageReadbackV1 Readback(DebianDeploymentPlanV1 plan, DebianStage stage) => new(plan.Root.GenerationId,
        DebianDeploymentPlanning.Fingerprint(plan), stage, DebianDeploymentStages.RequiredChecks(stage)
            .Select(c => new DebianCheckEvidenceV1(c, ObservationAvailability.Available, "FixtureIndependentReadback", Hash, DebianCheckVerdict.Satisfied)).ToImmutableArray());

    internal sealed class Runtime(TargetRootFixture.State state) : IDebianDeploymentOperations, IDebianDeploymentObserver
    {
        internal List<DebianStage> Invoked { get; } = [];
        internal DebianStage? FailAt { get; set; }
        internal DebianStage? ThrowAt { get; set; }
        internal DebianStage? ReadUnavailableAt { get; set; }
        internal DebianStage? GuardUnavailableAt { get; set; }
        internal Func<DebianStage, DebianRuntimeContextV1, DebianRuntimeContextV1>? ChangeContext { get; set; }
        internal Func<DebianStageReadbackV1, DebianStageReadbackV1>? ChangeReadback { get; set; }
        public Task<int> PerformAsync(DebianDeploymentPlanV1 plan, DebianStage stage, ResolvedInstallationV1 currentDevices, CancellationToken ct)
        {
            Invoked.Add(stage);
            if (ThrowAt == stage) throw new IOException("Fixture operation may have written before throwing.");
            return Task.FromResult(FailAt == stage ? 1 : 0);
        }
        public Task<DebianRuntimeContextV1> ObserveContextAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct)
        {
            var context = Context(state, plan, stage);
            if (GuardUnavailableAt == stage) context = context with { Inventory = Observations.Failure<InstallerRuntimeInventoryV1>(ObservationAvailability.Unavailable, "FixtureUnavailable") };
            return Task.FromResult(ChangeContext?.Invoke(stage, context) ?? context);
        }
        public Task<Observation<DebianStageReadbackV1>> InspectAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct)
        {
            var readback = Readback(plan, stage);
            if (FailAt == stage && DebianDeploymentStages.IsReadOnly(stage)) readback = readback with { Checks = readback.Checks.Select(c => c with { Verdict = DebianCheckVerdict.Mismatch }).ToImmutableArray() };
            return Task.FromResult(ReadUnavailableAt == stage ? Observations.Failure<DebianStageReadbackV1>(ObservationAvailability.Unavailable, "FixtureUnavailable") : A(ChangeReadback?.Invoke(readback) ?? readback));
        }
    }

    internal sealed class Journal : IDebianDeploymentJournal
    {
        internal List<byte[]> Checkpoints { get; } = [];
        internal bool FailReopen { get; set; }
        private bool _reserved;
        public Task BeginNewAsync(Guid generation, string planSha256, CancellationToken ct)
        {
            if (_reserved) throw new IOException("Generation already reserved, no blind retry.");
            _reserved = true;
            return Task.CompletedTask;
        }
        public Task<string> AppendDurablyAsync(Guid generation, ReadOnlyMemory<byte> checkpoint, CancellationToken ct)
        {
            Checkpoints.Add(checkpoint.ToArray());
            return Task.FromResult((Checkpoints.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        public Task<byte[]> ReopenAsync(string reference, CancellationToken ct)
        {
            if (FailReopen) throw new IOException("Fixture reopen failure.");
            return Task.FromResult(Checkpoints[int.Parse(reference, System.Globalization.CultureInfo.InvariantCulture)].ToArray());
        }
    }
}
