using System.Collections.Immutable;
using System.Globalization;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web.Services;

/// <summary>
/// Deterministic example records for Development only. Every chain is internally consistent:
/// a dry run references a real profile revision, its evidence carries a decision that follows
/// the Server's rules, and approvals and plans point back at exact evidence hashes.
/// Nothing here is reachable outside the Development environment (see Program.cs).
/// </summary>
internal sealed class SampleFleetData
{
    private const string Operator = "engineering-operator";
    private readonly Lock _syncRoot = new();

    public SampleFleetData()
    {
        Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(DateTimeOffset.UtcNow.Hour), TimeSpan.Zero);
        Seed();
    }

    public DateTimeOffset Now { get; }

    public Dictionary<Guid, TrustedDeviceView> Enrollments { get; } = [];
    public Dictionary<Guid, EnrollmentTokenInfo> Tokens { get; } = [];
    public Dictionary<Guid, MigrationProfileRevision> Profiles { get; } = [];
    public Dictionary<Guid, ReadOnlyWorkItem> Work { get; } = [];
    public Dictionary<Guid, DryRunEvidence> Evidence { get; } = [];
    public Dictionary<Guid, PlanApproval> Approvals { get; } = [];
    public Dictionary<Guid, PreparedMigrationPlan> Plans { get; } = [];
    public List<FleetAuditEvent> Audit { get; } = [];

    public T Read<T>(Func<SampleFleetData, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_syncRoot)
        {
            return read(this);
        }
    }

    public T Transact<T>(Func<SampleFleetData, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_syncRoot)
        {
            return update(this);
        }
    }

    public void Record(string actorType, string actorId, string action, Guid targetId, Guid correlationId, DateTimeOffset? at = null) =>
        Audit.Add(new FleetAuditEvent(Id(12, Audit.Count + 1), at ?? DateTimeOffset.UtcNow, actorType, actorId, action, targetId, correlationId));

    /// <summary>Deterministic, realistic-looking identifiers so sample rows are distinguishable by their first eight characters.</summary>
    public static Guid Id(int family, int number)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"igloo-fleet-sample:{family}:{number}")));
        var guid = bytes[..16];
        guid[7] = (byte)((guid[7] & 0x0F) | 0x40);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }

    public static string Hex64(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("igloo-fleet-sample:" + seed));
        return Convert.ToHexString(bytes);
    }

    private void Seed()
    {
        // Enrollments. Heartbeat ages and certificate windows are relative so the sample stays current.
        AddEnrollment(1, AgentTrustStatus.Active, Now.AddHours(-2), Now.AddDays(300), "1.4.0", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1);
        AddEnrollment(2, AgentTrustStatus.Active, Now.AddMinutes(-35), Now.AddDays(200), "1.4.0", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1);
        AddEnrollment(3, AgentTrustStatus.Active, Now.AddMinutes(-3), Now.AddDays(10), "1.3.2", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1);
        AddEnrollment(4, AgentTrustStatus.Disabled, Now.AddDays(-6), Now.AddDays(120), "1.3.2", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.EvidenceV1);
        AddEnrollment(5, AgentTrustStatus.Revoked, Now.AddDays(-40), Now.AddDays(-30), "1.2.0", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1);
        AddEnrollment(6, AgentTrustStatus.Active, null, Now.AddDays(90), null, null);
        AddEnrollment(7, AgentTrustStatus.Active, Now.AddDays(-10), Now.AddDays(-2), "1.4.0", "0.2-alpha",
            PlanningCapability.PreflightV1, PlanningCapability.EvidenceV1);
        AddEnrollment(8, AgentTrustStatus.Disabled, Now.AddDays(-90), Now.AddDays(-100), "1.1.5", "0.1-alpha",
            PlanningCapability.PreflightV1);

        // Enrollment tokens.
        AddToken(1, Now.AddMinutes(-5), Now.AddMinutes(5), consumed: false, revoked: false);
        AddToken(2, Now.AddDays(-21), Now.AddDays(-21).AddMinutes(30), consumed: true, revoked: false);
        AddToken(3, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(60), consumed: false, revoked: true);
        AddToken(4, Now.AddHours(-3), Now.AddHours(-2), consumed: false, revoked: false);
        AddToken(5, Now.AddMinutes(-1), Now.AddMinutes(59), consumed: false, revoked: false);

        // Migration profiles. Profile 1 has two revisions; revision 2 supersedes approvals made against revision 1.
        var debianR1 = AddProfile(1, 1, 1, "Debian 13 workstation", "debian", 25L * 1024 * 1024 * 1024, requireSecureBoot: false, Now.AddDays(-20));
        var debianR2 = AddProfile(1, 2, 2, "Debian 13 workstation", "debian", 25L * 1024 * 1024 * 1024, requireSecureBoot: true, Now.AddDays(-3));
        var fedoraR1 = AddProfile(2, 3, 1, "Fedora KDE lab", "fedora-kde", 40L * 1024 * 1024 * 1024, requireSecureBoot: true, Now.AddDays(-15));
        var mintR1 = AddProfile(3, 4, 1, "Linux Mint desktop", "linuxmint-cinnamon", 20L * 1024 * 1024 * 1024, requireSecureBoot: false, Now.AddDays(-12));

        // Work, evidence, approvals and plans.
        var w1 = AddWork(1, 1, ReadOnlyWorkType.RunAssessment, null, Now.AddDays(-5), 60, ReadOnlyWorkStatus.Completed, 1);
        AddAssessmentEvidence(w1, Now.AddDays(-5).AddMinutes(4), Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 16, 1);

        var w2 = AddWork(2, 1, ReadOnlyWorkType.RunMigrationDryRun, debianR1, Now.AddDays(-4), 60, ReadOnlyWorkStatus.Completed, 1);
        var e2 = AddDryRunEvidence(w2, Now.AddDays(-4).AddMinutes(6),
            Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 16, 1,
            new DryRunFacts(true, true, 180L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024, Hex64("plugin-debian"), []),
            PlanningEligibility.Eligible, []);
        var a1 = AddApproval(1, e2, Now.AddDays(-4).AddMinutes(30), null, ApprovalState.Superseded);
        AddPlan(1, a1, debianR1, e2, PreparedPlanState.Superseded, Now.AddDays(-4).AddMinutes(35));
        Record("Server", "input-change", "ApprovalInvalidated", a1.ApprovalId, w2.CorrelationId, Now.AddDays(-3));
        Record("Server", "input-change", "PreparedPlanInvalidated", Id(11, 1), w2.CorrelationId, Now.AddDays(-3));

        var w3 = AddWork(3, 1, ReadOnlyWorkType.RunMigrationDryRun, debianR2, Now.AddHours(-2), 120, ReadOnlyWorkStatus.Completed, 1);
        var e3 = AddDryRunEvidence(w3, Now.AddHours(-2).AddMinutes(5),
            Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Unknown, CheckStatus.Passed, CheckStatus.Passed), 16, 1,
            new DryRunFacts(true, true, 180L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024, Hex64("plugin-debian"), []),
            PlanningEligibility.NeedsReview, [PlanningReason.BitLockerUnknown, PlanningReason.CompatibilityReview]);
        var a2 = AddApproval(2, e3, Now.AddHours(-1), "BitLocker state confirmed off by the site technician on the device itself; no recovery key dependency.", ApprovalState.Approved);
        AddPlan(2, a2, debianR2, e3, PreparedPlanState.Prepared, Now.AddHours(-1).AddMinutes(3));

        var w4 = AddWork(4, 2, ReadOnlyWorkType.RunAssessment, null, Now.AddDays(-3), 60, ReadOnlyWorkStatus.Completed, 1);
        AddAssessmentEvidence(w4, Now.AddDays(-3).AddMinutes(3), Checks(CheckStatus.Passed, CheckStatus.Information, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 32, 2);

        var w5 = AddWork(5, 2, ReadOnlyWorkType.RunMigrationDryRun, fedoraR1, Now.AddDays(-1), 60, ReadOnlyWorkStatus.Completed, 1);
        AddDryRunEvidence(w5, Now.AddDays(-1).AddMinutes(7),
            Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Warning), 32, 2,
            new DryRunFacts(true, true, 12L * 1024 * 1024 * 1024, 40L * 1024 * 1024 * 1024, Hex64("plugin-fedora-kde"), [PlanningReason.InsufficientSpace]),
            PlanningEligibility.Blocked, [PlanningReason.CompatibilityReview, PlanningReason.InsufficientSpace]);

        AddWork(6, 3, ReadOnlyWorkType.RunMigrationDryRun, debianR2, Now.AddMinutes(-10), 60, ReadOnlyWorkStatus.Pending, 0);
        AddWork(7, 3, ReadOnlyWorkType.RunAssessment, null, Now.AddMinutes(-3), 60, ReadOnlyWorkStatus.Leased, 1, leaseMinutesRemaining: 2);
        AddWork(8, 7, ReadOnlyWorkType.RunAssessment, null, Now.AddDays(-2), 60, ReadOnlyWorkStatus.Expired, 0);

        var w9 = AddWork(9, 4, ReadOnlyWorkType.RunAssessment, null, Now.AddDays(-7), 60, ReadOnlyWorkStatus.Completed, 1);
        AddAssessmentEvidence(w9, Now.AddDays(-7).AddMinutes(5), Checks(CheckStatus.Warning, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 8, 1);

        var w10 = AddWork(10, 1, ReadOnlyWorkType.RunMigrationDryRun, mintR1, Now.AddDays(-6), 60, ReadOnlyWorkStatus.Completed, 2);
        AddFailedEvidence(w10, Now.AddDays(-6).AddMinutes(2));

        var w11 = AddWork(11, 2, ReadOnlyWorkType.RunMigrationDryRun, debianR2, Now.AddMinutes(-30), 120, ReadOnlyWorkStatus.Completed, 1);
        AddDryRunEvidence(w11, Now.AddMinutes(-25),
            Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 32, 2,
            new DryRunFacts(true, true, 410L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024, Hex64("plugin-debian"), []),
            PlanningEligibility.Eligible, []);

        var w12 = AddWork(12, 5, ReadOnlyWorkType.RunMigrationDryRun, debianR1, Now.AddDays(-45), 60, ReadOnlyWorkStatus.Completed, 1);
        var e12 = AddDryRunEvidence(w12, Now.AddDays(-45).AddMinutes(5),
            Checks(CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Passed), 16, 1,
            new DryRunFacts(true, false, 90L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024, Hex64("plugin-debian"), []),
            PlanningEligibility.Eligible, []);
        var a3 = AddApproval(3, e12, Now.AddDays(-45).AddMinutes(20), null, ApprovalState.Revoked);
        AddPlan(3, a3, debianR1, e12, PreparedPlanState.Invalidated, Now.AddDays(-45).AddMinutes(25));
        Record("Operator", Operator, "PreparedPlanInvalidated", Id(11, 3), w12.CorrelationId, Now.AddDays(-40));
        Record("Operator", Operator, "ApprovalRevoked", a3.ApprovalId, w12.CorrelationId, Now.AddDays(-40));

        Audit.Sort((left, right) => left.AtUtc.CompareTo(right.AtUtc));
    }

    private void AddEnrollment(int number, AgentTrustStatus status, DateTimeOffset? lastHeartbeat, DateTimeOffset certificateExpires,
        string? agentVersion, string? iglooVersion, params PlanningCapability[] capabilities)
    {
        var identity = new DeviceIdentity(Id(1, number), Id(2, number));
        var heartbeat = agentVersion is null
            ? null
            : new TrustedHeartbeat(PlanningProtocol.Version, agentVersion, iglooVersion!, [.. capabilities]);
        Enrollments[identity.DeviceId] = new TrustedDeviceView(identity, Id(3, number), Hex64("certificate-" + number),
            certificateExpires, status, lastHeartbeat, heartbeat);
        Record("Enrollment", Id(4, 100 + number).ToString("D"), "DeviceEnrolled", identity.DeviceId, Id(8, 100 + number), Now.AddDays(-(number * 9 + 3)));
    }

    private void AddToken(int number, DateTimeOffset created, DateTimeOffset expires, bool consumed, bool revoked)
    {
        var id = Id(4, number);
        Tokens[id] = new EnrollmentTokenInfo(id, created, expires, Operator, consumed, revoked);
        Record("Operator", Operator, "EnrollmentTokenCreated", id, id, created);
        if (revoked)
        {
            Record("Operator", Operator, "EnrollmentTokenRevoked", id, id, created.AddMinutes(10));
        }
    }

    private MigrationProfileRevision AddProfile(int profileNumber, int revisionNumber, int revision, string name, string distroId,
        long minimumBytes, bool requireSecureBoot, DateTimeOffset created)
    {
        var profile = new MigrationProfileRevision(Id(5, profileNumber), Id(6, revisionNumber), revision, 1,
            new MigrationProfileSpec(name, distroId, minimumBytes, requireSecureBoot), created);
        Profiles[profile.RevisionId] = profile;
        Record("Operator", Operator, revision == 1 ? "MigrationProfileCreated" : "MigrationProfileRevised", profile.RevisionId, profile.ProfileId, created);
        return profile;
    }

    private ReadOnlyWorkItem AddWork(int number, int enrollmentNumber, ReadOnlyWorkType type, MigrationProfileRevision? profile,
        DateTimeOffset created, int lifetimeMinutes, ReadOnlyWorkStatus status, int attempts, int? leaseMinutesRemaining = null)
    {
        var identity = Enrollments[Id(1, enrollmentNumber)].Identity;
        var leased = status == ReadOnlyWorkStatus.Leased;
        var work = new ReadOnlyWorkItem(Id(7, number), identity, type, created, created, created.AddMinutes(lifetimeMinutes),
            status, attempts, Id(8, number), 1, profile,
            leased ? Id(9, 500 + number) : null,
            leased ? Now.AddMinutes(leaseMinutesRemaining ?? 5) : null);
        Work[work.WorkItemId] = work;
        Record("Operator", Operator, type == ReadOnlyWorkType.RunAssessment ? "AssessmentRequested" : "DryRunRequested", work.WorkItemId, work.CorrelationId, created);
        return work;
    }

    private static ImmutableArray<PreflightCheckResult> Checks(CheckStatus firmware, CheckStatus secureBoot, CheckStatus tpm,
        CheckStatus bitLocker, CheckStatus memory, CheckStatus disks) =>
    [
        new(CheckId.Firmware, firmware), new(CheckId.SecureBoot, secureBoot), new(CheckId.Tpm, tpm),
        new(CheckId.BitLocker, bitLocker), new(CheckId.Memory, memory), new(CheckId.Disks, disks),
    ];

    private static EligibilityStatus AssessmentEligibility(ImmutableArray<PreflightCheckResult> checks, AssessmentOutcome outcome)
    {
        if (checks.Any(c => c.Status == CheckStatus.Blocked))
        {
            return EligibilityStatus.Blocked;
        }

        return outcome != AssessmentOutcome.Assessed || checks.IsEmpty || checks.Any(c => c.Status is CheckStatus.Warning or CheckStatus.Unknown)
            ? EligibilityStatus.NeedsReview
            : EligibilityStatus.ReadyForReview;
    }

    private AssessmentResult BuildAssessment(ReadOnlyWorkItem work, DateTimeOffset completed, ImmutableArray<PreflightCheckResult> checks,
        int ramGib, int diskCount, AssessmentOutcome outcome)
    {
        var heartbeat = Enrollments[work.Identity.DeviceId].Heartbeat!;
        return new AssessmentResult(FleetProtocolVersion.Current, work.WorkItemId, work.Identity, completed.AddMinutes(-2), completed,
            heartbeat.AgentVersion, heartbeat.IglooVersion, 1,
            new DeviceInventorySummary(outcome == AssessmentOutcome.Assessed ? ramGib * 1024L * 1024 * 1024 : 0, outcome == AssessmentOutcome.Assessed ? diskCount : 0),
            checks, AssessmentEligibility(checks, outcome), outcome);
    }

    private void AddAssessmentEvidence(ReadOnlyWorkItem work, DateTimeOffset completed, ImmutableArray<PreflightCheckResult> checks, int ramGib, int diskCount)
    {
        var assessment = BuildAssessment(work, completed, checks, ramGib, diskCount, AssessmentOutcome.Assessed);
        var result = new ReadOnlyWorkResult(PlanningProtocol.Version, work.WorkItemId, Id(9, 500 + work.WorkItemId.GetHashCode() % 97), work.CorrelationId, null, assessment, null);
        var blocked = checks.Any(c => c.Status == CheckStatus.Blocked);
        var reasons = new List<PlanningReason> { PlanningReason.MissingEvidence };
        if (blocked)
        {
            reasons.Add(PlanningReason.PrecheckFailed);
        }

        if (checks.Any(c => c.Status is CheckStatus.Warning or CheckStatus.Unknown))
        {
            reasons.Add(PlanningReason.CompatibilityReview);
        }

        reasons.Sort();
        var decision = new PlanningDecision(Id(9, work.WorkItemId.GetHashCode() % 1000 + 1), blocked ? PlanningEligibility.Blocked : PlanningEligibility.Unknown, [.. reasons], 1);
        Evidence[work.WorkItemId] = new DryRunEvidence(work.WorkItemId, result, Hex64("evidence-" + work.WorkItemId), completed, decision,
            Enrollments[work.Identity.DeviceId].Heartbeat!.Capabilities);
        Record("Agent", work.Identity.AgentId.ToString("D"), "AssessmentReceived", work.WorkItemId, work.CorrelationId, completed);
    }

    private DryRunEvidence AddDryRunEvidence(ReadOnlyWorkItem work, DateTimeOffset completed, ImmutableArray<PreflightCheckResult> checks,
        int ramGib, int diskCount, DryRunFacts facts, PlanningEligibility eligibility, ImmutableArray<PlanningReason> reasons)
    {
        var assessment = BuildAssessment(work, completed, checks, ramGib, diskCount, AssessmentOutcome.Assessed);
        var result = new ReadOnlyWorkResult(PlanningProtocol.Version, work.WorkItemId, Id(9, 600 + work.WorkItemId.GetHashCode() % 97), work.CorrelationId,
            work.Profile!.RevisionId, assessment, facts);
        var decision = new PlanningDecision(Id(9, work.WorkItemId.GetHashCode() % 1000 + 1), eligibility, reasons, 1);
        var evidence = new DryRunEvidence(work.WorkItemId, result, Hex64("evidence-" + work.WorkItemId), completed, decision,
            Enrollments[work.Identity.DeviceId].Heartbeat!.Capabilities);
        Evidence[work.WorkItemId] = evidence;
        Record("Agent", work.Identity.AgentId.ToString("D"), "AssessmentReceived", work.WorkItemId, work.CorrelationId, completed);
        Record("Agent", work.Identity.AgentId.ToString("D"), "DryRunCompleted", work.WorkItemId, work.CorrelationId, completed);
        Record("Server", "rules-v1", "EligibilityEvaluated", decision.DecisionId, work.CorrelationId, completed);
        return evidence;
    }

    private void AddFailedEvidence(ReadOnlyWorkItem work, DateTimeOffset completed)
    {
        var assessment = BuildAssessment(work, completed, [], 0, 0, AssessmentOutcome.LocalExecutionFailed);
        var result = new ReadOnlyWorkResult(PlanningProtocol.Version, work.WorkItemId, Id(9, 700), work.CorrelationId, work.Profile!.RevisionId, assessment, null);
        var decision = new PlanningDecision(Id(9, 701), PlanningEligibility.Unknown, [PlanningReason.MissingEvidence, PlanningReason.LocalReadFailure], 1);
        Evidence[work.WorkItemId] = new DryRunEvidence(work.WorkItemId, result, Hex64("evidence-" + work.WorkItemId), completed, decision,
            Enrollments[work.Identity.DeviceId].Heartbeat!.Capabilities);
        Record("Agent", work.Identity.AgentId.ToString("D"), "AssessmentReceived", work.WorkItemId, work.CorrelationId, completed);
        Record("Agent", work.Identity.AgentId.ToString("D"), "DryRunCompleted", work.WorkItemId, work.CorrelationId, completed);
        Record("Server", "rules-v1", "EligibilityEvaluated", decision.DecisionId, work.CorrelationId, completed);
    }

    private PlanApproval AddApproval(int number, DryRunEvidence evidence, DateTimeOffset approved, string? reason, ApprovalState state)
    {
        var work = Work[evidence.DryRunId];
        var approval = new PlanApproval(Id(10, number), work.Identity, evidence.DryRunId, work.Profile!.RevisionId, evidence.Decision.DecisionId,
            evidence.EvidenceHash, approved, approved.AddHours(4), Operator, reason, state);
        Approvals[approval.ApprovalId] = approval;
        Record("Operator", Operator, "PlanApproved", approval.ApprovalId, work.CorrelationId, approved);
        return approval;
    }

    private void AddPlan(int number, PlanApproval approval, MigrationProfileRevision profile, DryRunEvidence evidence, PreparedPlanState state, DateTimeOffset created)
    {
        var plan = new PreparedMigrationPlan(Id(11, number), approval.Identity, profile.RevisionId, evidence.DryRunId, evidence.Decision.DecisionId,
            approval.ApprovalId, evidence.EvidenceHash, created, approval.ExpiresAtUtc, evidence.Result.Assessment.IglooVersion, profile.Spec.DistroId,
            evidence.Result.Planning!.PluginHash!, state,
            "Non-executable planning artifact. Recovery readiness and fresh execution authorization required in a future phase.");
        Plans[plan.PlanId] = plan;
        Record("Operator", Operator, "PreparedPlanCreated", plan.PlanId, Work[evidence.DryRunId].CorrelationId, created);
    }
}
