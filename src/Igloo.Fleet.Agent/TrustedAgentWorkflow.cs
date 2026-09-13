using System.Net;
using System.Net.Http.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

public sealed class TrustedAgentWorkflow(HttpClient client, ReadOnlyPlanner planner, ResultSpool spool)
{
    public async Task RunOnceAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        var heartbeat = new TrustedHeartbeat(PlanningProtocol.Version, ReadOnlyAssessment.AgentVersion, ReadOnlyAssessment.IglooVersion,
            [PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1]);
        using var response = await client.PostAsJsonAsync("v2/agent/heartbeat", heartbeat, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);
        foreach (var pending in spool.Pending())
        {
            if (pending.Assessment.Identity != identity) throw new InvalidDataException("Spool identity mismatch.");
            await SubmitAsync(pending, ct).ConfigureAwait(false);
        }
        using var claim = await client.PostAsync(new Uri("v2/agent/work/claim", UriKind.Relative), null, ct).ConfigureAwait(false);
        await CheckAsync(claim, ct).ConfigureAwait(false);
        if (claim.StatusCode == HttpStatusCode.NoContent) return;
        var work = await claim.Content.ReadFromJsonAsync<ReadOnlyWorkItem>(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing work payload.");
        if (work.Identity != identity) throw new InvalidDataException("Work identity mismatch.");
        var result = await planner.RunAsync(work, ct).ConfigureAwait(false);
        spool.Save(result); // Persist before the first transmission, not only after a failure.
        await SubmitAsync(result, ct).ConfigureAwait(false);
    }

    private async Task SubmitAsync(ReadOnlyWorkResult result, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var submitted = await client.PostAsJsonAsync("v2/agent/results", result, ct).ConfigureAwait(false);
                if ((int)submitted.StatusCode >= 500) throw new HttpRequestException("Fleet server temporarily unavailable.");
                await CheckAsync(submitted, ct).ConfigureAwait(false);
                spool.Acknowledge(result.WorkItemId);
                return;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode) return;
        var error = await response.Content.ReadFromJsonAsync<FleetError>(ct).ConfigureAwait(false);
        throw new FleetProtocolException(error ?? new(FleetErrorCode.ServerFailure, "Fleet request failed."));
    }
}
