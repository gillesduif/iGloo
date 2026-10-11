using System.Text.Json;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

/// <summary>Trusted work uses CLI commands; the Host constructor is retained for fixture/transition parity.</summary>
public sealed class ReadOnlyPlanner
{
    private readonly IEndpointInspection? _inspection;
    private readonly IProviderCompatibility? _providers;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>>? _invoke;

    public ReadOnlyPlanner(IEndpointInspection inspection, IProviderCompatibility providers)
    {
        _inspection = inspection;
        _providers = providers;
    }

    public ReadOnlyPlanner(CliCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _invoke = (request, ct) => client.ExecuteAsync(request, cancellationToken: ct);
    }

    internal ReadOnlyPlanner(Func<CommandRequest, CancellationToken, Task<CommandResult>> invoke) =>
        _invoke = invoke;

    public async Task<ReadOnlyWorkResult> RunAsync(ReadOnlyWorkItem work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.PayloadVersion != 1 || !Enum.IsDefined(work.Type) || work.LeaseId is null ||
            (work.Type == ReadOnlyWorkType.RunMigrationDryRun && work.Profile?.SchemaVersion != 1))
            throw new InvalidDataException("Unsupported read-only work.");
        var started = DateTimeOffset.UtcNow;
        try { return await ObserveAsync(work, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or JsonException or System.Security.SecurityException)
        {
            var failed = new AssessmentResult(FleetProtocolVersion.Current, work.WorkItemId, work.Identity,
                started, DateTimeOffset.UtcNow, ReadOnlyAssessment.AgentVersion, ReadOnlyAssessment.IglooVersion,
                1, new(0, 0), [], EligibilityStatus.NeedsReview, AssessmentOutcome.LocalExecutionFailed);
            return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId.Value, work.CorrelationId,
                work.Profile?.RevisionId, failed, null);
        }
    }

    private async Task<ReadOnlyWorkResult> ObserveAsync(ReadOnlyWorkItem work, CancellationToken ct)
    {
        if (_invoke is not null) return await ObserveViaCliAsync(work, ct).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        var report = await _inspection!.InspectAsync(ct).ConfigureAwait(false);
        var assessment = ReadOnlyAssessment.Map(report, work.Identity, started) with { AssessmentId = work.WorkItemId };
        DryRunFacts? facts = null;
        if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
        {
            var spec = work.Profile!.Spec;
            var reasons = new HashSet<PlanningReason>();
            var supported = _providers!.IsLoaded(spec.DistroId) && Igloo.Fleet.Domain.PlanningService.SupportedDistro(spec.DistroId);
            var required = spec.MinimumAvailableBytes;
            string? pluginHash = null;
            if (supported)
            {
                var provider = await _providers.InspectAsync(spec.DistroId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Distro provider became unavailable.");
                required = Math.Max(required, provider.MinimumDiskBytes);
                // Only this read-only plugin method is called. No rendering or payload staging.
                var findings = _providers.Evaluate(spec.DistroId, report);
                if (findings.Any(f => f.Severity == FindingSeverity.Blocker)) reasons.Add(PlanningReason.HardwareUnsupported);
                if (findings.Any(f => f.Severity == FindingSeverity.Warning)) reasons.Add(PlanningReason.CompatibilityReview);
                pluginHash = provider.ImplementationHash;
            }
            else reasons.Add(PlanningReason.UnsupportedDistro);
            // A deterministic candidate: the single disk with Windows' boot partition.
            // GetSupportedSize values are observations, not permission to shrink.
            var available = AvailableCapacity(report.Disks, required);
            facts = new(supported, report.SecureBootEnabled, available, required, pluginHash, [.. reasons.Order()]);
        }
        return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value, work.CorrelationId,
            work.Profile?.RevisionId, assessment, facts);
    }

    public static long? AvailableCapacity(IReadOnlyList<DiskInfo> disks, long required) =>
        MigrationCapacityEvaluator.Evaluate(disks, required).ObservedAvailableBytes;

    private async Task<ReadOnlyWorkResult> ObserveViaCliAsync(ReadOnlyWorkItem work,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var inspect = await QueryAsync("system.inspect", new { }, 1, ct).ConfigureAwait(false);
        var summary = inspect.Data?.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)
            ?? throw new InvalidDataException("System inspection returned no data.");
        if (summary.Findings is null || summary.DiskCount < 0 ||
            string.IsNullOrWhiteSpace(summary.BitLockerState) ||
            !IsFingerprint(inspect.ObservedStateFingerprint))
            throw new InvalidDataException("System inspection returned invalid advisory data.");
        var assessment = ReadOnlyAssessment.Map(summary, work.Identity, started) with
        { AssessmentId = work.WorkItemId };
        DryRunFacts? facts = null;
        if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
        {
            var spec = work.Profile!.Spec;
            var reasons = new HashSet<PlanningReason>();
            var supported = Igloo.Fleet.Domain.PlanningService.SupportedDistro(spec.DistroId);
            var required = spec.MinimumAvailableBytes;
            long? available = null;
            string? providerHash = null;
            if (supported)
            {
                var planResult = await _invoke!(CliCommandClient.Request("migration.plan",
                    new MigrationPlanInput(spec.DistroId, required), capabilityVersion: 2), ct)
                    .ConfigureAwait(false);
                if (planResult.Error?.Code == CommandErrorCode.NotSupported)
                    supported = false;
                else
                {
                    if (!planResult.Succeeded) throw new IOException("CLI planning failed.");
                    var plan = planResult.Data?.Deserialize<MigrationPlanResultV2>(CommandProtocol.JsonOptions)
                        ?? throw new InvalidDataException("CLI planning returned no data.");
                    if (plan.PlanId == Guid.Empty || plan.Executable ||
                        plan.Disposition != "AdvisoryOnly" ||
                        !IsFingerprint(plan.ProviderRevisionFingerprint) ||
                        !IsFingerprint(plan.ObservedStateFingerprint) ||
                        !string.Equals(plan.ObservedStateFingerprint,
                            planResult.ObservedStateFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(plan.ObservedMachineFingerprint,
                            inspect.ObservedStateFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(plan.DistroId, spec.DistroId,
                            StringComparison.OrdinalIgnoreCase) ||
                        plan.RequiredBytes < required || plan.ObservedAvailableBytes < 0 ||
                        plan.BlockerCodes is null || !Enum.IsDefined(plan.CapacityState) ||
                        (plan.CapacityState == MigrationCapacityDisposition.KnownAvailable &&
                            plan.ObservedAvailableBytes < plan.RequiredBytes))
                        throw new InvalidDataException("CLI planning returned invalid advisory data.");
                    required = plan.RequiredBytes;
                    available = plan.ObservedAvailableBytes;
                    providerHash = plan.ProviderRevisionFingerprint;
                    var compatibilityResult = await QueryAsync("distro.compatibility",
                        new DistroSelection(spec.DistroId), 1, ct).ConfigureAwait(false);
                    var compatibility = compatibilityResult.Data?.Deserialize<DistroCompatibility>(
                        CommandProtocol.JsonOptions) ?? throw new InvalidDataException(
                        "CLI compatibility returned no data.");
                    if (!string.Equals(compatibility.DistroId, spec.DistroId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(compatibilityResult.ObservedStateFingerprint,
                            inspect.ObservedStateFingerprint, StringComparison.Ordinal) ||
                        compatibility.Findings is null ||
                        compatibility.HasBlockers != compatibility.Findings.Any(
                            f => f.Severity == "Blocker"))
                        throw new InvalidDataException("CLI compatibility target changed.");
                    if (compatibility.Findings.Any(f => f.Severity == "Blocker"))
                        reasons.Add(PlanningReason.HardwareUnsupported);
                    if (compatibility.Findings.Any(f => f.Severity == "Warning"))
                        reasons.Add(PlanningReason.CompatibilityReview);
                }
            }
            if (!supported) reasons.Add(PlanningReason.UnsupportedDistro);
            facts = new(supported, summary.SecureBootEnabled, available, required,
                providerHash, [.. reasons.Order()]);
        }
        return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value,
            work.CorrelationId, work.Profile?.RevisionId, assessment, facts);
    }

    private async Task<CommandResult> QueryAsync(string capability, object input, int version,
        CancellationToken ct)
    {
        var result = await _invoke!(CliCommandClient.Request(capability, input,
            capabilityVersion: version), ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException("CLI query failed.");
        return result;
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
