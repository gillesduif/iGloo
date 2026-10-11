using System.Security.Cryptography;
using System.Text;
using Igloo.Command.Contracts;

namespace Igloo.Core.Runtime.Execution;

// Internal, hypothetical complete intent. Computing its digest is not approval, preparation,
// authority, recovery readiness or a means to produce any of the required identities.
internal sealed record MigrationExecutionIntentV1(Guid PlanId, Guid TargetId, Guid AllocationId,
    Guid LayoutId, string ArtifactId, Guid RecoveryId, string PlanRevision,
    string ProviderRevision, string TargetRevision, string AllocationRevision,
    string LayoutRevision, string RecoveryRevision, RiskClass Risk);

internal static class MigrationExecutionIntentFingerprint
{
    internal static string Compute(MigrationExecutionIntentV1 intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.PlanId == Guid.Empty || intent.TargetId == Guid.Empty ||
            intent.AllocationId == Guid.Empty || intent.LayoutId == Guid.Empty ||
            intent.RecoveryId == Guid.Empty || intent.Risk != RiskClass.Destructive ||
            !ValidArtifact(intent.ArtifactId) || !ValidDigest(intent.PlanRevision) ||
            !ValidDigest(intent.ProviderRevision) || !ValidDigest(intent.TargetRevision) ||
            !ValidDigest(intent.AllocationRevision) || !ValidDigest(intent.LayoutRevision) ||
            !ValidDigest(intent.RecoveryRevision))
            throw new ArgumentException("A complete destructive execution intent is required.",
                nameof(intent));

        // Fixed field order and encoding are independent of C# JSON naming and culture. The
        // version tag prevents a future different set of bound facts reusing the same identity.
        var canonical = string.Join('\n',
            "igloo.migration.execution-intent.v1",
            intent.PlanId.ToString("N"), intent.TargetId.ToString("N"),
            intent.AllocationId.ToString("N"), intent.LayoutId.ToString("N"),
            intent.ArtifactId, intent.RecoveryId.ToString("N"),
            intent.PlanRevision, intent.ProviderRevision, intent.TargetRevision,
            intent.AllocationRevision, intent.LayoutRevision, intent.RecoveryRevision,
            "destructive") + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool ValidArtifact(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256-", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool ValidDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
