using System.Net.Http.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web;

/// <summary>Read-only API client for a future engineering status host; no UI framework is selected.</summary>
public sealed class FleetStatusClient(HttpClient client)
{
    public Task<FleetVersion?> GetVersionAsync(CancellationToken ct = default) =>
        client.GetFromJsonAsync<FleetVersion>("health", ct);

    public Task<RegisteredDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        client.GetFromJsonAsync<RegisteredDevice>($"agents/{deviceId}", ct);

    public Task<AssessmentResult?> GetAssessmentAsync(Guid assessmentId, CancellationToken ct = default) =>
        client.GetFromJsonAsync<AssessmentResult>($"assessments/{assessmentId}", ct);
}
