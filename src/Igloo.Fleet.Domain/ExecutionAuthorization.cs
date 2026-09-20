using System.Collections.Immutable;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public enum ExecutionOperationType { StorageResize, BootConfiguration }
public sealed record AuthorizedOperation(Guid OperationId, ExecutionOperationType Type);
public sealed record ExecutionBinding(Guid ExecutionId, Guid PlanId, DeviceIdentity Endpoint, Guid ProfileRevisionId,
    string EvidenceHash, int TargetSchema, string TargetFingerprint, ImmutableArray<AuthorizedOperation> Operations);
public sealed record ExecutionAuthorization(int SchemaVersion, Guid AuthorizationId, ExecutionBinding Binding,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
public enum AuthorizationStatus { Valid, Missing, Malformed, Unavailable, Expired, NotYetValid, Consumed, CorrelationMismatch, AlreadyIssued }
public sealed record AuthorizationValidation(AuthorizationStatus Status, ExecutionAuthorization? Authority = null);

public static class ExecutionAuthorizationRules
{
    public static bool WellFormed(ExecutionBinding? binding) => binding is not null && binding.ExecutionId != Guid.Empty &&
        binding.PlanId != Guid.Empty && binding.Endpoint is not null && binding.Endpoint.DeviceId != Guid.Empty &&
        binding.Endpoint.AgentId != Guid.Empty && binding.ProfileRevisionId != Guid.Empty && binding.TargetSchema == 1 &&
        !string.IsNullOrWhiteSpace(binding.EvidenceHash) && !string.IsNullOrWhiteSpace(binding.TargetFingerprint) &&
        !binding.Operations.IsDefaultOrEmpty && binding.Operations.All(o => o is not null && o.OperationId != Guid.Empty && Enum.IsDefined(o.Type)) &&
        binding.Operations.Select(o => o.OperationId).Distinct().Count() == binding.Operations.Length;

    public static bool Equivalent(ExecutionBinding? left, ExecutionBinding? right) => left is not null && right is not null && WellFormed(left) && WellFormed(right) &&
        EvidenceIntegrity.Hash(left! with { Operations = left!.Operations.OrderBy(o => o.OperationId).ToImmutableArray() }) ==
        EvidenceIntegrity.Hash(right! with { Operations = right!.Operations.OrderBy(o => o.OperationId).ToImmutableArray() });

    public static AuthorizationStatus Validate(ExecutionAuthorization? authority, ExecutionBinding expected, DateTimeOffset now)
    {
        if (authority is null || authority.SchemaVersion != 1 || authority.AuthorizationId == Guid.Empty || !WellFormed(authority.Binding) ||
            authority.IssuedAtUtc.Offset != TimeSpan.Zero || authority.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            authority.ExpiresAtUtc <= authority.IssuedAtUtc || authority.ExpiresAtUtc - authority.IssuedAtUtc > TimeSpan.FromMinutes(10))
            return AuthorizationStatus.Malformed;
        if (!Equivalent(authority.Binding, expected)) return AuthorizationStatus.CorrelationMismatch;
        if (now < authority.IssuedAtUtc) return AuthorizationStatus.NotYetValid;
        return now >= authority.ExpiresAtUtc ? AuthorizationStatus.Expired : AuthorizationStatus.Valid;
    }
}
