using System.Net.Http.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Protocol failures must carry a machine-readable FleetError; message-only constructors lose that invariant.")]
public sealed class FleetProtocolException(FleetError error) : Exception(error.Diagnostic)
{
    public FleetError Error { get; } = error;
}

/// <summary>Communication failure never triggers endpoint remediation.</summary>
public sealed class FleetAssessmentClient(HttpClient client, ReadOnlyAssessment assessment)
{
    public async Task<AssessmentResult> RunAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        var registration = new AgentRegistrationRequest(FleetProtocolVersion.Current, identity,
            ReadOnlyAssessment.AgentVersion, ReadOnlyAssessment.IglooVersion, new(true));
        using var enrolled = await client.PostAsJsonAsync("agents", registration, ct).ConfigureAwait(false);
        await CheckAsync(enrolled, ct).ConfigureAwait(false);
        var accepted = await enrolled.Content.ReadFromJsonAsync<AgentRegistrationResponse>(ct).ConfigureAwait(false);
        if (accepted is null || !accepted.Protocol.IsSupported || accepted.Identity != identity)
            throw new FleetProtocolException(new(FleetErrorCode.UnsupportedProtocol, "Server registration response is incompatible."));
        using var heartbeat = await client.PostAsJsonAsync("heartbeats",
            new AgentHeartbeat(FleetProtocolVersion.Current, identity, new(true)), ct).ConfigureAwait(false);
        await CheckAsync(heartbeat, ct).ConfigureAwait(false);
        var result = await assessment.RunAsync(identity, ct).ConfigureAwait(false);
        using var submitted = await client.PostAsJsonAsync("assessments", result, ct).ConfigureAwait(false);
        await CheckAsync(submitted, ct).ConfigureAwait(false);
        return result;
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var error = await response.Content.ReadFromJsonAsync<FleetError>(ct).ConfigureAwait(false);
        throw new FleetProtocolException(error ?? new(FleetErrorCode.ServerFailure, "Fleet request failed."));
    }
}
