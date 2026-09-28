using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianDeploymentRunV1(int SchemaVersion, DebianDeploymentPlanV1 Plan,
    string PlanSha256, ImmutableArray<DebianStageResultV1> Stages)
{
    [JsonIgnore]
    public DebianDeploymentState State => Stages.Any(s => s.Outcome is DebianStageOutcome.OutcomeUnknown or DebianStageOutcome.IntentDurable)
        ? DebianDeploymentState.OutcomeUnknown : Stages.Any(s => s.Outcome == DebianStageOutcome.Failed)
        ? DebianDeploymentState.Failed : Stages.IsEmpty || Stages.All(s => s.Outcome == DebianStageOutcome.NotStarted) ? DebianDeploymentState.NotStarted :
        Stages.Length == DebianDeploymentStages.Ordered.Length && Stages.All(s => s.Outcome == DebianStageOutcome.AppliedAndVerified)
        ? DebianDeploymentState.EvidenceComplete : Stages.Length == (int)DebianStage.ConfigureSignedPackages &&
            Stages.All(s => s.Outcome == DebianStageOutcome.AppliedAndVerified)
        ? DebianDeploymentState.PreBootStagesVerified : DebianDeploymentState.InProgress;
}

// Separate performer and observer are intentional. A performer result alone cannot satisfy a
// stage; all operations require independent fresh readback. There is NO production implementer.
public interface IDebianDeploymentOperations
{
    Task<int> PerformAsync(DebianDeploymentPlanV1 plan, DebianStage stage, ResolvedInstallationV1 currentDevices, CancellationToken ct);
}
public interface IDebianDeploymentObserver
{
    Task<DebianRuntimeContextV1> ObserveContextAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct);
    Task<Observation<DebianStageReadbackV1>> InspectAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct);
}
public interface IDebianDeploymentJournal
{
    // Reserve generation exclusively, refusing an existing/partial run. No automatic resume.
    Task BeginNewAsync(Guid generation, string planSha256, CancellationToken ct);
    // Create-new/flush/close before returning. Reopen must use a different handle/process reader.
    Task<string> AppendDurablyAsync(Guid generation, ReadOnlyMemory<byte> checkpoint, CancellationToken ct);
    Task<byte[]> ReopenAsync(string reference, CancellationToken ct);
}

// Candidate orchestration only: injected operations are not a production capability flag. No
// native executor or reboot call is supplied. A future backend must implement the whole contract.
public sealed class DebianDeploymentEngine(IDebianDeploymentOperations operations,
    IDebianDeploymentObserver observer, IDebianDeploymentJournal journal)
{
    private bool _started;

    public async Task<DebianDeploymentRunV1> RunAsync(DebianDeploymentPlanV1 plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_started) throw new InvalidOperationException("A deployment engine cannot retry or resume a run.");
        var hash = DebianDeploymentPlanning.Fingerprint(plan);
        _started = true;
        var run = new DebianDeploymentRunV1(1, plan, hash, []);
        await journal.BeginNewAsync(plan.Root.GenerationId, hash, ct).ConfigureAwait(false);
        foreach (var stage in DebianDeploymentStages.Ordered)
        {
            ct.ThrowIfCancellationRequested();
            var context = await observer.ObserveContextAsync(plan, stage, ct).ConfigureAwait(false);
            var guard = DebianTargetBoundary.Verify(plan, stage, context);
            if (guard.Availability != ObservationAvailability.Available)
            {
                run = run with { Stages = run.Stages.Add(new(stage, DebianStageOutcome.NotStarted, null,
                    guard.Code!, [new(DebianCheck.Ownership, guard.Availability, guard.Code!, null, DebianCheckVerdict.Unassessed)])) };
                await CheckpointAsync(run, ct).ConfigureAwait(false);
                return run;
            }
            run = run with { Stages = run.Stages.Add(new(stage, DebianStageOutcome.IntentDurable, null, "IntentBeforePossibleSideEffects", [])) };
            // If durability/reopen fails, no call for this stage is made. If later persistence
            // fails, the last durable intent remains OutcomeUnknown, never "not applied".
            await CheckpointAsync(run, ct).ConfigureAwait(false);
            // Durability can take time. Acquire again after reopen, immediately before calling
            // the operation; a mount/device change while persisting must prevent that call.
            context = await observer.ObserveContextAsync(plan, stage, ct).ConfigureAwait(false);
            guard = DebianTargetBoundary.Verify(plan, stage, context);
            if (guard.Availability != ObservationAvailability.Available)
            {
                run = run with { Stages = run.Stages.SetItem(run.Stages.Length - 1, new(stage, DebianStageOutcome.NotStarted, null,
                    guard.Code!, [new(DebianCheck.Ownership, guard.Availability, guard.Code!, null, DebianCheckVerdict.Unassessed)])) };
                await CheckpointAsync(run, ct).ConfigureAwait(false);
                return run;
            }
            int? exitCode = null;
            string? error = null;
            if (!DebianDeploymentStages.IsReadOnly(stage))
            {
                try { exitCode = await operations.PerformAsync(plan, stage, guard.Value, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OperationCanceledException)
                { error = ex.GetType().Name; }
            }
            Observation<DebianStageReadbackV1> readback;
            // Still attempt readback after a failed command; cancellation must not erase possible
            // partial effects. A separate bounded observer must implement its own read timeout.
            try { readback = await observer.InspectAsync(plan, stage, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OperationCanceledException)
            { readback = Observations.Failure<DebianStageReadbackV1>(ObservationErrors.Classify(ex), ex.GetType().Name); }
            var valid = readback.Availability == ObservationAvailability.Available &&
                DebianDeploymentStages.ValidReadback(readback.Value, plan.Root.GenerationId, hash, stage);
            var allAvailable = valid && readback.Value.Checks.All(c => c.Availability == ObservationAvailability.Available);
            var satisfied = allAvailable && readback.Value.Checks.All(c => c.Verdict == DebianCheckVerdict.Satisfied);
            var outcome = error is not null || !allAvailable ? DebianStageOutcome.OutcomeUnknown :
                exitCode is not (null or 0) || !satisfied ? DebianStageOutcome.Failed : DebianStageOutcome.AppliedAndVerified;
            run = run with { Stages = run.Stages.SetItem(run.Stages.Length - 1, new(stage, outcome, exitCode,
                error ?? (valid ? satisfied ? "IndependentReadbackRecorded" : "ReadbackCheckFailedOrUnknown" : "ReadbackUnavailableOrSubstituted"),
                valid ? readback.Value.Checks : []) { Commands = operations is IDebianCommandEvidenceSource source ? source.Evidence(stage) : [] }) };
            await CheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
            if (outcome != DebianStageOutcome.AppliedAndVerified) return run;
        }
        return run;
    }

    private async Task CheckpointAsync(DebianDeploymentRunV1 run, CancellationToken ct)
    {
        var bytes = DebianDeploymentArtifacts.Serialize(run);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var reference = await journal.AppendDurablyAsync(run.Plan.Root.GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await journal.ReopenAsync(reference, ct).ConfigureAwait(false);
        _ = DebianDeploymentArtifacts.Reopen(reopened, hash, run.Plan.Root.GenerationId, run.PlanSha256);
    }
}

public static class DebianDeploymentArtifacts
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Serialize(DebianDeploymentRunV1 run)
    {
        RequireStructure(run);
        return JsonSerializer.SerializeToUtf8Bytes(run, Options);
    }

    public static DebianDeploymentRunV1 Reopen(ReadOnlySpan<byte> bytes, string hash, Guid generation, string planHash)
    {
        if (bytes.Length is <= 0 or > 16 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("Debian checkpoint integrity mismatch.");
        var run = JsonSerializer.Deserialize<DebianDeploymentRunV1>(bytes, Options) ?? throw new InvalidDataException("Missing Debian checkpoint.");
        RequireStructure(run);
        if (run.Plan.Root.GenerationId != generation || run.PlanSha256 != planHash)
            throw new InvalidDataException("Debian checkpoint generation or plan changed.");
        return run;
    }

    public static void RequireStructure(DebianDeploymentRunV1 run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SchemaVersion != 1 || run.Plan is null || run.PlanSha256 != DebianDeploymentPlanning.Fingerprint(run.Plan) ||
            run.Stages.IsDefault || run.Stages.Length > DebianDeploymentStages.Ordered.Length ||
            !run.Stages.Select(s => s.Stage).SequenceEqual(DebianDeploymentStages.Ordered.Take(run.Stages.Length)))
            throw new InvalidDataException("Invalid Debian checkpoint structure.");
        foreach (var (stage, index) in run.Stages.Select((s, i) => (s, i)))
        {
            if (!Enum.IsDefined(stage.Outcome) || stage.Checks.IsDefault || stage.Commands.IsDefault || string.IsNullOrWhiteSpace(stage.Code) ||
                stage.Commands.Any(c => c.GenerationId != run.Plan.Root.GenerationId || c.PlanSha256 != run.PlanSha256 || c.Stage != stage.Stage ||
                    !DebianDeploymentPlanning.Hash(c.OperationSha256) || !DebianDeploymentPlanning.Hash(c.ToolSha256) ||
                    !Enum.IsDefined(c.State) || !c.Executable.StartsWith("/usr/", StringComparison.Ordinal) ||
                    (c.State == DebianCommandState.Exited ? c.ExitCode is null : c.ExitCode is not null) ||
                    (stage.Outcome == DebianStageOutcome.AppliedAndVerified && (c.State != DebianCommandState.Exited || c.ExitCode != 0))) ||
                (index < run.Stages.Length - 1 && stage.Outcome != DebianStageOutcome.AppliedAndVerified) ||
                (stage.Outcome == DebianStageOutcome.AppliedAndVerified && (stage.ExitCode is not (null or 0) ||
                    !DebianDeploymentStages.ValidReadback(new(run.Plan.Root.GenerationId, run.PlanSha256, stage.Stage, stage.Checks),
                        run.Plan.Root.GenerationId, run.PlanSha256, stage.Stage) || stage.Checks.Any(c => c.Availability != ObservationAvailability.Available || c.Verdict != DebianCheckVerdict.Satisfied))) ||
                (stage.Outcome is DebianStageOutcome.NotStarted or DebianStageOutcome.IntentDurable && stage.ExitCode is not null))
                throw new InvalidDataException("Invalid Debian checkpoint stage sequence.");
        }
    }
}

public static class DebianDeploymentSupport
{
    public static Observation<bool> Production => Observations.Failure<bool>(ObservationAvailability.Unsupported,
        "DebianOfflineClosureIsolationAgentAndPermanentFirmwareFinalizerUnqualified");
}
