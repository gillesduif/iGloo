using System.Collections.Immutable;

namespace Igloo.Distro.Debian.Deployment;

// Product policy, not a rewritten Debian dependency and not an authentication claim.
public sealed record DebianRecommendationExceptionV1(string Id, string Release, string RepositoryId,
    string Suite, string Package, string Version, string Architecture, string Kind, string Relation,
    string Reason, string ProductImpact, Uri EvidenceUri, DateTimeOffset ReviewedUtc,
    DateTimeOffset ReviewBeforeUtc, string ReviewCondition);

public sealed record DebianUnresolvedRelationV1(string RepositoryId, string Suite, string Package,
    string Version, string Architecture, string Kind, string Relation, bool CandidateAvailable);
public sealed record DebianAppliedRecommendationExceptionV1(string ExceptionId, DebianUnresolvedRelationV1 Relation);
public sealed record DebianDependencyClosureReportV1(int SchemaVersion,
    ImmutableArray<DebianUnresolvedRelationV1> OriginalUnresolved,
    ImmutableArray<DebianAppliedRecommendationExceptionV1> AppliedExceptions,
    ImmutableArray<DebianUnresolvedRelationV1> RemainingUnresolved);

public static class DebianRecommendationPolicy
{
    public static DebianRecommendationExceptionV1 Wsdd { get; } = new(
        "trixie-gvfs-wsdd-2026-09-28-v1", "trixie", "debian", "trixie", "gvfs-backends",
        "1.57.2-2+deb13u1", "amd64", "Recommends", "wsdd",
        "The authenticated Trixie repositories contain no wsdd candidate. Debian bug 1110689 and the authenticated GVfs source separate WS-Discovery from SMB client access.",
        "Automatic WS-Discovery browsing of some Windows/Samba shares is unavailable. Direct smb://server/share access, local files, GNOME startup and iGloo offline migration do not require wsdd. Network discovery is not mandatory in this workstation baseline.",
        new("https://bugs.debian.org/1110689"), new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
        new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero),
        "Review before expiry or on any package/version/relation/repository change. A candidate or provider becoming available disables this exception; no wsdd2 substitution.");

    public static bool Matches(DebianUnresolvedRelationV1 relation) => relation is not null &&
        relation.RepositoryId == Wsdd.RepositoryId && relation.Suite == Wsdd.Suite &&
        relation.Package == Wsdd.Package && relation.Version == Wsdd.Version &&
        relation.Architecture == Wsdd.Architecture && relation.Kind == Wsdd.Kind &&
        relation.Relation == Wsdd.Relation && !relation.CandidateAvailable;

    // Structural correspondence only. The native independent solver must check the signed
    // relationships, candidate/provider absence, freshness and the full Depends closure.
    internal static bool ValidReport(OfflineDebianPackageSetV1 set)
    {
        var report = set.DependencyPolicy;
        if (report is null || report.SchemaVersion != 1 || report.OriginalUnresolved.IsDefault ||
            report.AppliedExceptions.IsDefault || report.RemainingUnresolved.IsDefault || !report.RemainingUnresolved.IsEmpty ||
            report.OriginalUnresolved.Length > 1 ||
            report.OriginalUnresolved.Length != report.AppliedExceptions.Length) return false;
        if (report.OriginalUnresolved.IsEmpty) return true;
        var relation = report.OriginalUnresolved[0];
        return Matches(relation) && report.AppliedExceptions[0] == new DebianAppliedRecommendationExceptionV1(Wsdd.Id, relation) &&
            set.Packages.Any(p => p.RepositoryId == relation.RepositoryId && p.Name == relation.Package &&
                p.Version == relation.Version && p.Architecture == relation.Architecture) &&
            !set.Packages.Any(p => p.Name == "wsdd");
    }
}
