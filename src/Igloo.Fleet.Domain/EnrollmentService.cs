using System.Security.Cryptography;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed partial class PlanningService(IPlanningStore store, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public EnrollmentTokenCreated CreateToken(int lifetimeMinutes, string actor)
    {
        if (lifetimeMinutes is < 1 or > 60)
            throw new PlanningException(FleetErrorCode.InvalidRequest);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return store.Transact(state =>
        {
            var id = Guid.NewGuid();
            var expiry = Now.AddMinutes(lifetimeMinutes);
            state.Tokens.Add(id, new(id, EvidenceIntegrity.SecretHash(token), Now, expiry, actor, false, false));
            Audit(state, "Operator", actor, "EnrollmentTokenCreated", id, id);
            return new EnrollmentTokenCreated(id, token, expiry);
        });
    }

    public void RevokeToken(Guid id, string actor) => store.Transact(state =>
    {
        var token = Get(state.Tokens, id);
        state.Tokens[id] = token with { Revoked = true };
        Audit(state, "Operator", actor, "EnrollmentTokenRevoked", id, id);
        return true;
    });

    public EnrollmentResponse Enroll(string token, Func<DeviceIdentity, IssuedAgentCertificate> issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (token is not { Length: 64 })
            throw new PlanningException(FleetErrorCode.EnrollmentTokenInvalid);
        var hash = EvidenceIntegrity.SecretHash(token);
        return store.Transact(state =>
        {
            var found = state.Tokens.Values.SingleOrDefault(t => t.Hash == hash)
                ?? throw new PlanningException(FleetErrorCode.EnrollmentTokenInvalid);
            if (found.Revoked) throw new PlanningException(FleetErrorCode.EnrollmentTokenRevoked);
            if (found.Consumed) throw new PlanningException(FleetErrorCode.EnrollmentTokenConsumed);
            if (found.ExpiresAtUtc <= Now) throw new PlanningException(FleetErrorCode.EnrollmentTokenExpired);
            var identity = new DeviceIdentity(Guid.NewGuid(), Guid.NewGuid());
            var certificate = issue(identity);
            if (state.Devices.Values.Any(d => d.CertificateHash == certificate.CertificateHash))
                throw new PlanningException(FleetErrorCode.Conflict);
            var enrollment = Guid.NewGuid();
            state.Devices.Add(identity.DeviceId, new(identity, enrollment, certificate.CertificateHash,
                certificate.ExpiresAtUtc, AgentTrustStatus.Active, null, null));
            state.Tokens[found.TokenId] = found with { Consumed = true };
            Audit(state, "Enrollment", found.TokenId.ToString(), "DeviceEnrolled", identity.DeviceId, enrollment);
            return new EnrollmentResponse(identity, enrollment, certificate.CertificatePem);
        });
    }

    public TrustedDeviceView Authenticate(string certificateHash) => store.Read(state =>
    {
        var device = state.Devices.Values.SingleOrDefault(d => d.CertificateHash == certificateHash)
            ?? throw new PlanningException(FleetErrorCode.CertificateInvalid);
        Active(device);
        return device;
    });

    public void SetTrust(Guid deviceId, AgentTrustStatus status, string actor) => store.Transact(state =>
    {
        if (!Enum.IsDefined(status)) throw new PlanningException(FleetErrorCode.InvalidRequest);
        var device = Get(state.Devices, deviceId);
        if (device.Status == AgentTrustStatus.Revoked) throw new PlanningException(FleetErrorCode.CertificateRevoked);
        state.Devices[deviceId] = device with { Status = status };
        Invalidate(state, deviceId, null);
        Audit(state, "Operator", actor, status switch
        {
            AgentTrustStatus.Active => "AgentReenabled", AgentTrustStatus.Disabled => "AgentDisabled", _ => "AgentRevoked",
        }, deviceId, deviceId);
        return true;
    });

    public void Heartbeat(DeviceIdentity identity, TrustedHeartbeat heartbeat) => store.Transact(state =>
    {
        var device = Authorized(state, identity);
        if (heartbeat.Protocol != PlanningProtocol.Version)
            throw new PlanningException(FleetErrorCode.UnsupportedProtocol);
        if (heartbeat.AgentVersion != "0.1.0" || !Version.TryParse(heartbeat.IglooVersion, out _) ||
            heartbeat.IglooVersion.Length > 32 || heartbeat.Capabilities.IsDefault ||
            heartbeat.Capabilities.Length > 4 || heartbeat.Capabilities.Any(c => !Enum.IsDefined(c)) ||
            heartbeat.Capabilities.Distinct().Count() != heartbeat.Capabilities.Length)
            throw new PlanningException(FleetErrorCode.CapabilityUnsupported);
        if (device.Heartbeat is { } previous &&
            (previous.AgentVersion != heartbeat.AgentVersion || previous.IglooVersion != heartbeat.IglooVersion ||
            !previous.Capabilities.SequenceEqual(heartbeat.Capabilities)))
            Invalidate(state, identity.DeviceId, null);
        state.Devices[identity.DeviceId] = device with { LastHeartbeatUtc = Now, Heartbeat = heartbeat };
        return true;
    });

    public IReadOnlyList<TrustedDeviceView> Devices() => store.Read(s => s.Devices.Values.ToArray());
    public IReadOnlyList<EnrollmentTokenInfo> Tokens() => store.Read(s => s.Tokens.Values
        .Select(t => new EnrollmentTokenInfo(t.TokenId, t.CreatedAtUtc, t.ExpiresAtUtc, t.CreatedBy, t.Consumed, t.Revoked)).ToArray());
    public IReadOnlyList<FleetAuditEvent> AuditEvents() => store.Read(s => s.Audit.ToArray());
    public void RecordDenial(string action) => store.Transact(state =>
    {
        Audit(state, "Unknown", "unauthenticated", action, Guid.Empty, Guid.Empty);
        return true;
    });

    private TrustedDeviceView Authorized(PlanningState state, DeviceIdentity identity)
    {
        var device = Get(state.Devices, identity.DeviceId);
        if (device.Identity != identity) throw new PlanningException(FleetErrorCode.CertificateInvalid);
        Active(device);
        return device;
    }

    private void Active(TrustedDeviceView device)
    {
        if (device.Status == AgentTrustStatus.Revoked) throw new PlanningException(FleetErrorCode.CertificateRevoked);
        if (device.Status == AgentTrustStatus.Disabled) throw new PlanningException(FleetErrorCode.AgentDisabled);
        if (device.CertificateExpiresAtUtc <= Now) throw new PlanningException(FleetErrorCode.CertificateInvalid);
    }

    private void Audit(PlanningState state, string actorType, string actor, string action, Guid target, Guid correlation) =>
        state.Audit.Add(new(Guid.NewGuid(), Now, actorType, actor, action, target, correlation));
    private static T Get<T>(Dictionary<Guid, T> items, Guid id) =>
        items.TryGetValue(id, out var item) ? item : throw new PlanningException(FleetErrorCode.NotFound);

    private void Invalidate(PlanningState state, Guid? deviceId, Guid? profileId)
    {
        foreach (var approval in state.Approvals.Values.Where(a => a.Status == ApprovalState.Approved &&
            (deviceId == a.Identity.DeviceId || (profileId.HasValue &&
             state.Profiles[a.ProfileRevisionId].ProfileId == profileId))).ToArray())
        {
            state.Approvals[approval.ApprovalId] = approval with { Status = ApprovalState.Superseded };
            Audit(state, "Server", "input-change", "ApprovalInvalidated", approval.ApprovalId, state.Work[approval.DryRunId].CorrelationId);
        }
        foreach (var plan in state.Plans.Values.Where(p => p.Status == PreparedPlanState.Prepared &&
            state.Approvals[p.ApprovalId].Status != ApprovalState.Approved).ToArray())
        {
            state.Plans[plan.PlanId] = plan with { Status = PreparedPlanState.Superseded };
            Audit(state, "Server", "input-change", "PreparedPlanInvalidated", plan.PlanId, state.Work[plan.DryRunId].CorrelationId);
        }
    }
}
