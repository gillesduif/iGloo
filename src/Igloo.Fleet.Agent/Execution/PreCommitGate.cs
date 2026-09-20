using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Fleet.Agent.Targets;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent.Execution;

public enum RecoveryReadinessStatus { Ready, NotReady, ObservationUnavailable, Unsupported, Ambiguous }
public enum RecoveryBlockReason { NotImplemented, BootEvidenceUnavailable, WinReUnavailable, BitLockerUnavailable, RestorationUnproven }
public sealed record RecoveryReadiness(RecoveryReadinessStatus Status, ImmutableArray<RecoveryBlockReason> Reasons)
{
    public static RecoveryReadiness Production => new(RecoveryReadinessStatus.ObservationUnavailable, [RecoveryBlockReason.NotImplemented]);
}
public enum GateStatus { Ready, Blocked }
public enum GateBlockReason { PlanInvalid, BindingRequired, BindingCorrelationMismatch, TargetNotExact, ProtectedStateUnavailable,
    ProtectedStateCorrelationMismatch, RecoveryNotReady, AuthorizationInvalid, AuthorizationCorrelationMismatch, InvalidOperationSet }
public sealed record PreCommitInputs(ExecutionBinding Expected, PlanValidity PlanValidity, ExactTargetBinding? ApprovedTarget,
    Observation<WindowsStorageSnapshot> Storage, Observation<BitLockerVolumeObservation> BitLocker,
    ProtectedStateVerification ProtectedState, RecoveryReadiness Recovery, AuthorizationValidation Authorization, DateTimeOffset Now);
public sealed record PreCommitResult(GateStatus Status, ImmutableArray<GateBlockReason> Reasons, TargetRevalidation? Target, AuthorizationStatus Authorization,
    ProtectedStateStatus ProtectedState, RecoveryReadiness Recovery);

// Evaluated local inputs only. Callers must acquire fresh trusted snapshots; this is not a capability or execution API.
public static class PreCommitGate
{
    public static PreCommitResult Evaluate(PreCommitInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reasons = ImmutableArray.CreateBuilder<GateBlockReason>();
        var expected = input.Expected;
        var plan = input.PlanValidity.Plan;
        if (!ExecutionAuthorizationRules.WellFormed(expected)) reasons.Add(GateBlockReason.InvalidOperationSet);
        if (!input.PlanValidity.IsValid || input.PlanValidity.EvaluatedAtUtc != input.Now || plan is null || plan.Status != PreparedPlanState.Prepared || plan.ExpiresAtUtc <= input.Now)
            reasons.Add(GateBlockReason.PlanInvalid);
        var binding = input.ApprovedTarget;
        if (binding is null) reasons.Add(GateBlockReason.BindingRequired);
        if (plan is null || binding is null || expected.PlanId != plan.PlanId || expected.Endpoint != plan.Identity ||
            expected.ProfileRevisionId != plan.ProfileRevisionId || expected.EvidenceHash != plan.EvidenceHash ||
            expected.TargetSchema != binding.SchemaVersion || expected.TargetFingerprint != binding.TargetFingerprint)
            reasons.Add(GateBlockReason.BindingCorrelationMismatch);
        TargetRevalidation? target = plan is null ? null : TargetRevalidator.Compare(plan, binding, input.Storage, input.BitLocker);
        if (target?.Outcome != TargetMatch.ExactMatch) reasons.Add(GateBlockReason.TargetNotExact);
        if (input.ProtectedState.Status != ProtectedStateStatus.Verified) reasons.Add(GateBlockReason.ProtectedStateUnavailable);
        if (!ExecutionAuthorizationRules.Equivalent(input.ProtectedState.Authority.Binding, expected))
            reasons.Add(GateBlockReason.ProtectedStateCorrelationMismatch);
        if (input.Recovery.Status != RecoveryReadinessStatus.Ready || input.Recovery.Reasons.IsDefault || input.Recovery.Reasons.Length != 0)
            reasons.Add(GateBlockReason.RecoveryNotReady);
        var authorizationStatus = input.Authorization.Status == AuthorizationStatus.Valid
            ? ExecutionAuthorizationRules.Validate(input.Authorization.Authority, expected, input.Now) : input.Authorization.Status;
        if (authorizationStatus != AuthorizationStatus.Valid)
            reasons.Add(GateBlockReason.AuthorizationInvalid);
        if (!ExecutionAuthorizationRules.Equivalent(input.Authorization.Authority?.Binding, expected))
            reasons.Add(GateBlockReason.AuthorizationCorrelationMismatch);
        return new(reasons.Count == 0 ? GateStatus.Ready : GateStatus.Blocked, reasons.ToImmutable(), target, authorizationStatus, input.ProtectedState.Status, input.Recovery);
    }
}
