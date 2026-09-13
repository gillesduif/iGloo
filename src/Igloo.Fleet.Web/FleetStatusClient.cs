using System.Net.Http.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web;

/// <summary>Read-only API client for a future engineering status host; no UI framework is selected.</summary>
public sealed class FleetStatusClient(HttpClient client)
{
    public Task<TrustedDeviceView[]?> GetTrustedDevicesAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<TrustedDeviceView[]>("v2/operator/devices", ct);
    public Task<MigrationProfileRevision[]?> GetProfilesAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<MigrationProfileRevision[]>("v2/operator/profiles", ct);
    public Task<DryRunEvidence[]?> GetEvidenceAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<DryRunEvidence[]>("v2/operator/evidence", ct);
    public Task<PreparedMigrationPlan[]?> GetPlansAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<PreparedMigrationPlan[]>("v2/operator/plans", ct);
    public Task<FleetAuditEvent[]?> GetAuditAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<FleetAuditEvent[]>("v2/operator/audit", ct);
    public Task<FleetVersion?> GetVersionAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<FleetVersion>("health", ct);

    public Task<RegisteredDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        client.GetFromJsonAsync<RegisteredDevice>($"agents/{deviceId}", ct);

    public Task<AssessmentResult?> GetAssessmentAsync(Guid assessmentId, CancellationToken ct = default) =>
        client.GetFromJsonAsync<AssessmentResult>($"assessments/{assessmentId}", ct);
}
