using Igloo.Fleet.Web.Services;

namespace Igloo.Fleet.Web;

/// <summary>CSV export of the planning and audit collections, whole and unfiltered, like the enrollment exports.</summary>
internal static class PlanningExportEndpoints
{
    internal static IEndpointRouteBuilder MapPlanningExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/export/profiles.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetProfilesAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("profiles.csv",
                ["ProfileId", "RevisionId", "Revision", "Name", "DistroId", "MinimumAvailableBytes", "RequireSecureBoot", "CreatedUtc"],
                result.Value!.Select(p => new[] { p.ProfileId.ToString("D"), p.RevisionId.ToString("D"), p.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Spec.Name, p.Spec.DistroId, p.Spec.MinimumAvailableBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Spec.RequireSecureBoot.ToString(), ExportEndpoints.Iso(p.CreatedAtUtc) }));
        }).RequireAuthorization();

        endpoints.MapGet("/export/work-items.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetWorkItemsAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("work-items.csv",
                ["WorkItemId", "EnrollmentId", "Type", "Status", "CreatedUtc", "ExpiresUtc", "AttemptCount", "CorrelationId", "ProfileRevisionId"],
                result.Value!.Select(w => new[] { w.WorkItemId.ToString("D"), w.Identity.DeviceId.ToString("D"), w.Type.ToString(), w.Status.ToString(), ExportEndpoints.Iso(w.CreatedAtUtc), ExportEndpoints.Iso(w.ExpiresAtUtc), w.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture), w.CorrelationId.ToString("D"), w.Profile?.RevisionId.ToString("D") ?? string.Empty }));
        }).RequireAuthorization();

        endpoints.MapGet("/export/evidence.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetEvidenceAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("evidence.csv",
                ["WorkItemId", "EnrollmentId", "Type", "AssessmentOutcome", "Eligibility", "Reasons", "ReceivedUtc", "EvidenceHash", "DecisionId"],
                result.Value!.Select(e => new[] { e.DryRunId.ToString("D"), e.Result.Assessment.Identity.DeviceId.ToString("D"), e.Result.Planning is null ? "RunAssessment" : "RunMigrationDryRun", e.Result.Assessment.Eligibility.ToString(), e.Decision.Status.ToString(), string.Join(";", e.Decision.Reasons), ExportEndpoints.Iso(e.ReceivedAtUtc), e.EvidenceHash, e.Decision.DecisionId.ToString("D") }));
        }).RequireAuthorization();

        endpoints.MapGet("/export/approvals.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetApprovalsAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("approvals.csv",
                ["ApprovalId", "EnrollmentId", "Status", "ApprovedUtc", "ExpiresUtc", "ApprovedBy", "WorkItemId", "ProfileRevisionId", "DecisionId", "EvidenceHash", "ReviewReason"],
                result.Value!.Select(a => new[] { a.ApprovalId.ToString("D"), a.Identity.DeviceId.ToString("D"), a.Status.ToString(), ExportEndpoints.Iso(a.ApprovedAtUtc), ExportEndpoints.Iso(a.ExpiresAtUtc), a.ApprovedBy, a.DryRunId.ToString("D"), a.ProfileRevisionId.ToString("D"), a.DecisionId.ToString("D"), a.EvidenceHash, a.ReviewReason ?? string.Empty }));
        }).RequireAuthorization();

        endpoints.MapGet("/export/plans.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetPlansAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("plans.csv",
                ["PlanId", "EnrollmentId", "Status", "CreatedUtc", "ExpiresUtc", "DistroId", "IglooVersion", "ApprovalId", "WorkItemId", "ProfileRevisionId", "EvidenceHash", "PluginHash", "SafetyBoundary"],
                result.Value!.Select(p => new[] { p.PlanId.ToString("D"), p.Identity.DeviceId.ToString("D"), p.Status.ToString(), ExportEndpoints.Iso(p.CreatedAtUtc), ExportEndpoints.Iso(p.ExpiresAtUtc), p.DistroId, p.IglooVersion, p.ApprovalId.ToString("D"), p.DryRunId.ToString("D"), p.ProfileRevisionId.ToString("D"), p.EvidenceHash, p.PluginHash, p.SafetyBoundary }));
        }).RequireAuthorization();

        endpoints.MapGet("/export/audit.csv", async (IFleetOperatorClient client, CancellationToken ct) =>
        {
            var result = await client.GetAuditAsync(ct).ConfigureAwait(false);
            return !result.Succeeded ? Unavailable() : ExportEndpoints.Csv("audit.csv",
                ["EventId", "AtUtc", "ActorType", "ActorId", "Action", "TargetId", "CorrelationId"],
                result.Value!.Select(e => new[] { e.EventId.ToString("D"), ExportEndpoints.Iso(e.AtUtc), e.ActorType, e.ActorId, e.Action, e.TargetId.ToString("D"), e.CorrelationId.ToString("D") }));
        }).RequireAuthorization();

        return endpoints;
    }

    private static IResult Unavailable() => Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}
