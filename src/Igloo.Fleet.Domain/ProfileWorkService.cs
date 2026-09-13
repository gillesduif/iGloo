using System.Collections.Immutable;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed partial class PlanningService
{
    public static bool SupportedDistro(string? id) => id is "debian" or "fedora-kde" or "linuxmint-cinnamon";

    public MigrationProfileRevision SaveProfile(Guid? profileId, MigrationProfileSpec spec, string actor)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Name is not { Length: > 0 and <= 80 } || !SupportedDistro(spec.DistroId) ||
            spec.MinimumAvailableBytes is < 21474836480L or > 1099511627776L)
            throw new PlanningException(FleetErrorCode.ProfileInvalid);
        return store.Transact(state =>
        {
            var prior = state.Profiles.Values.Where(p => p.ProfileId == profileId).ToArray();
            if (profileId.HasValue && prior.Length == 0) throw new PlanningException(FleetErrorCode.NotFound);
            var profile = new MigrationProfileRevision(profileId ?? Guid.NewGuid(), Guid.NewGuid(),
                prior.Length + 1, 1, spec, Now);
            state.Profiles.Add(profile.RevisionId, profile);
            Invalidate(state, null, profile.ProfileId);
            Audit(state, "Operator", actor, prior.Length == 0 ? "MigrationProfileCreated" : "MigrationProfileRevised",
                profile.RevisionId, profile.ProfileId);
            return profile;
        });
    }

    public IReadOnlyList<MigrationProfileRevision> Profiles() => store.Read(s => s.Profiles.Values.ToArray());

    public ReadOnlyWorkItem RequestWork(RequestReadOnlyWork request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        return store.Transact(state =>
        {
            var device = Get(state.Devices, request.DeviceId);
            Active(device);
            if (!Enum.IsDefined(request.Type) || request.LifetimeMinutes is < 1 or > 1440)
                throw new PlanningException(FleetErrorCode.InvalidRequest);
            var required = RequiredCapabilities(request.Type);
            if (device.Heartbeat is null || required.Any(c => !device.Heartbeat.Capabilities.Contains(c)))
                throw new PlanningException(FleetErrorCode.CapabilityUnsupported);
            MigrationProfileRevision? profile = null;
            if (request.Type == ReadOnlyWorkType.RunMigrationDryRun)
            {
                profile = Get(state.Profiles, request.ProfileRevisionId ?? Guid.Empty);
                if (state.Profiles.Values.Any(p => p.ProfileId == profile.ProfileId && p.Revision > profile.Revision))
                    throw new PlanningException(FleetErrorCode.ProfileInvalid);
            }
            else if (request.ProfileRevisionId.HasValue)
                throw new PlanningException(FleetErrorCode.InvalidRequest);
            if (state.Work.Values.Count(w => w.Identity == device.Identity &&
                w.Status is ReadOnlyWorkStatus.Pending or ReadOnlyWorkStatus.Leased) >= 20)
                throw new PlanningException(FleetErrorCode.Conflict);
            var work = new ReadOnlyWorkItem(Guid.NewGuid(), device.Identity, request.Type, Now, Now,
                Now.AddMinutes(request.LifetimeMinutes), ReadOnlyWorkStatus.Pending, 0, Guid.NewGuid(), 1, profile, null, null);
            state.Work.Add(work.WorkItemId, work);
            Audit(state, "Operator", actor, request.Type == ReadOnlyWorkType.RunAssessment ? "AssessmentRequested" : "DryRunRequested",
                work.WorkItemId, work.CorrelationId);
            return work;
        });
    }

    public ReadOnlyWorkItem? Claim(DeviceIdentity identity) => store.Transact(state =>
    {
        var device = Authorized(state, identity);
        foreach (var expired in state.Work.Values.Where(w => w.Identity == identity && w.ExpiresAtUtc <= Now &&
            w.Status != ReadOnlyWorkStatus.Completed).ToArray())
            state.Work[expired.WorkItemId] = expired with { Status = ReadOnlyWorkStatus.Expired };
        var candidate = state.Work.Values.OrderBy(w => w.CreatedAtUtc).FirstOrDefault(w => w.Identity == identity &&
            w.NotBeforeUtc <= Now && w.ExpiresAtUtc > Now && w.AttemptCount < 5 &&
            (w.Status == ReadOnlyWorkStatus.Pending || (w.Status == ReadOnlyWorkStatus.Leased && w.LeaseExpiresAtUtc <= Now)) &&
            device.Heartbeat is not null && RequiredCapabilities(w.Type).All(device.Heartbeat.Capabilities.Contains));
        if (candidate is null) return null;
        var leased = candidate with { Status = ReadOnlyWorkStatus.Leased, AttemptCount = candidate.AttemptCount + 1,
            LeaseId = Guid.NewGuid(), LeaseExpiresAtUtc = Now.AddMinutes(5) };
        state.Work[leased.WorkItemId] = leased;
        return leased;
    });

    public DryRunEvidence Submit(DeviceIdentity identity, ReadOnlyWorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return store.Transact(state =>
        {
            var device = Authorized(state, identity);
            var work = Get(state.Work, result.WorkItemId);
            if (work.Identity != identity || result.Assessment?.Identity != identity)
                throw new PlanningException(FleetErrorCode.CertificateInvalid);
            if (result.Protocol != PlanningProtocol.Version) throw new PlanningException(FleetErrorCode.UnsupportedProtocol);
            if (result.CorrelationId != work.CorrelationId || result.ProfileRevisionId != work.Profile?.RevisionId)
                throw new PlanningException(FleetErrorCode.EvidenceInvalid);
            if (state.Evidence.TryGetValue(work.WorkItemId, out var existing))
            {
                if (EvidenceIntegrity.Hash(existing.Result) != EvidenceIntegrity.Hash(result))
                    throw new PlanningException(FleetErrorCode.Conflict);
                return existing;
            }
            if (work.Status != ReadOnlyWorkStatus.Leased || result.LeaseId != work.LeaseId)
                throw new PlanningException(FleetErrorCode.WorkExpired);
            ValidateResult(result, work, device);
            var decision = PlanningRules.Evaluate(result, work.Profile?.Spec);
            var hash = EvidenceIntegrity.Hash(new { Result = result, work.Profile, device.Heartbeat!.Capabilities, RulesVersion = 1 });
            var evidence = new DryRunEvidence(work.WorkItemId, result, hash, Now, decision, device.Heartbeat.Capabilities);
            // Late results remain durable history, but cannot be approved.
            Invalidate(state, identity.DeviceId, null);
            state.Evidence.Add(evidence.DryRunId, evidence);
            state.Work[work.WorkItemId] = work with { Status = ReadOnlyWorkStatus.Completed };
            Audit(state, "Agent", identity.AgentId.ToString(), "AssessmentReceived", evidence.DryRunId, work.CorrelationId);
            if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
            {
                Audit(state, "Agent", identity.AgentId.ToString(), "DryRunCompleted", evidence.DryRunId, work.CorrelationId);
                Audit(state, "Server", "rules-v1", "EligibilityEvaluated", decision.DecisionId, work.CorrelationId);
            }
            return evidence;
        });
    }

    public IReadOnlyList<DryRunEvidence> Evidence(Guid? deviceId = null) => store.Read(s =>
        s.Evidence.Values.Where(e => !deviceId.HasValue || e.Result.Assessment.Identity.DeviceId == deviceId).ToArray());
    public IReadOnlyList<ReadOnlyWorkItem> WorkItems() => store.Read(s => s.Work.Values.ToArray());

    private static ImmutableArray<PlanningCapability> RequiredCapabilities(ReadOnlyWorkType type) =>
        type == ReadOnlyWorkType.RunAssessment ? [PlanningCapability.PreflightV1, PlanningCapability.EvidenceV1] :
        [PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1];

    private void ValidateResult(ReadOnlyWorkResult result, ReadOnlyWorkItem work, TrustedDeviceView device)
    {
        var assessment = result.Assessment;
        if (assessment is null || !assessment.Protocol.IsSupported || assessment.PreflightSchema != 1 ||
            assessment.AssessmentId != work.WorkItemId || assessment.StartedAt == default ||
            assessment.StartedAt < work.CreatedAtUtc.AddMinutes(-5) ||
            assessment.StartedAt.Offset != TimeSpan.Zero || assessment.CompletedAt.Offset != TimeSpan.Zero ||
            assessment.CompletedAt < assessment.StartedAt || assessment.CompletedAt > Now.AddMinutes(5) ||
            device.Heartbeat is null || assessment.AgentVersion != device.Heartbeat.AgentVersion ||
            assessment.IglooVersion != device.Heartbeat.IglooVersion ||
            assessment.Inventory is null || assessment.Inventory.TotalRamBytes < 0 || assessment.Inventory.DiskCount is < 0 or > 1024 ||
            assessment.Checks.IsDefault || assessment.Checks.Length > 7 ||
            assessment.Checks.Any(c => c is null || !Enum.IsDefined(c.Id) || !Enum.IsDefined(c.Status)) ||
            assessment.Checks.Select(c => c.Id).Distinct().Count() != assessment.Checks.Length ||
            !Enum.IsDefined(assessment.Outcome) ||
            assessment.Eligibility != EligibilityDecision.Evaluate(assessment.Checks, assessment.Outcome))
            throw new PlanningException(FleetErrorCode.EvidenceInvalid);
        if (assessment.Outcome == AssessmentOutcome.Assessed &&
            Enumerable.Range(0, 6).Any(i => !assessment.Checks.Any(c => (int)c.Id == i)))
            throw new PlanningException(FleetErrorCode.EvidenceInvalid);
        if (assessment.Outcome == AssessmentOutcome.LocalExecutionFailed)
        {
            if (!assessment.Checks.IsEmpty || assessment.Inventory != new DeviceInventorySummary(0, 0) || result.Planning is not null)
                throw new PlanningException(FleetErrorCode.EvidenceInvalid);
            return;
        }
        if (work.Type == ReadOnlyWorkType.RunAssessment && result.Planning is not null)
            throw new PlanningException(FleetErrorCode.EvidenceInvalid);
        if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
        {
            var facts = result.Planning;
            if (facts is null || facts.AvailableBytes < 0 || facts.RequiredBytes < work.Profile!.Spec.MinimumAvailableBytes ||
                facts.Reasons.IsDefault || facts.Reasons.Length > 10 || facts.Reasons.Any(r => !Enum.IsDefined(r)) ||
                (facts.TargetSupported && (facts.PluginHash is not { Length: 64 } || !facts.PluginHash.All(Uri.IsHexDigit))) ||
                (!facts.TargetSupported && facts.PluginHash is not null))
                throw new PlanningException(FleetErrorCode.EvidenceInvalid);
        }
    }
}
