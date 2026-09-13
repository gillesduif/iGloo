using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Server;
using Igloo.Fleet.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class ServerTests
{
    private const string Token = "development-test-token-at-least-32-characters";

    [Fact]
    public async Task ActualHttpWorkflowEnrollsHeartbeatsStoresAndRetrievesEvidence()
    {
        await using var app = FleetServer.Build(Token, 0);
        await app.StartAsync();
        using var client = Client(app.Services);
        var status = new FleetStatusClient(client);
        Assert.True((await status.GetVersionAsync())!.DevelopmentOnly);
        var identity = new DeviceIdentity(Guid.NewGuid(), Guid.NewGuid());
        var result = await new FleetAssessmentClient(client, new ReadOnlyAssessment(new AgentTests.Checker())).RunAsync(identity);
        var retrieved = await status.GetAssessmentAsync(result.AssessmentId);
        Assert.Equal(result.Checks.ToArray(), retrieved!.Checks.ToArray());
        Assert.Equal(result.Identity, retrieved.Identity);
        Assert.Equal(identity, (await status.GetDeviceAsync(identity.DeviceId))!.Registration.Identity);
        using var duplicate = await client.PostAsJsonAsync("assessments", result);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var destructive = await client.PostAsJsonAsync("migrate-all", new { });
        Assert.Equal(HttpStatusCode.NotFound, destructive.StatusCode);
    }

    [Fact]
    public async Task RejectsMissingAuthUnsupportedProtocolInvalidEvidenceAndUnknownIdentity()
    {
        await using var app = FleetServer.Build(Token, 0);
        await app.StartAsync();
        using var client = Client(app.Services);
        client.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await client.GetAsync(new Uri("health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var identity = new DeviceIdentity(Guid.NewGuid(), Guid.NewGuid());
        var request = new AgentRegistrationRequest(new(99, 0), identity, ReadOnlyAssessment.AgentVersion,
            ReadOnlyAssessment.IglooVersion, new(true));
        using var unsupported = await client.PostAsJsonAsync("agents", request);
        Assert.Equal(FleetErrorCode.UnsupportedProtocol, (await unsupported.Content.ReadFromJsonAsync<FleetError>())!.Code);
        using var unknown = await client.PostAsJsonAsync("heartbeats", new AgentHeartbeat(FleetProtocolVersion.Current, identity, new(true)));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        using var registered = await client.PostAsJsonAsync("agents", request with { Protocol = FleetProtocolVersion.Current });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var result = ReadOnlyAssessment.Map(AgentTests.Report, identity, DateTimeOffset.UtcNow);
        foreach (var invalid in new[]
        {
            result with { Eligibility = EligibilityStatus.ReadyForReview },
            result with { Checks = ImmutableArray<PreflightCheckResult>.Empty },
            result with { CompletedAt = result.StartedAt.AddMinutes(-1) },
            result with { PreflightSchema = 99 },
        })
        {
            using var rejected = await client.PostAsJsonAsync("assessments", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var sensitive = await client.PostAsJsonAsync("agents", new
        {
            Protocol = FleetProtocolVersion.Current, Identity = identity, AgentVersion = "0.1.0",
            IglooVersion = "1.0.0", Capabilities = new AgentCapabilities(true), Password = "PRIVATE",
        });
        Assert.Equal(HttpStatusCode.BadRequest, sensitive.StatusCode);
    }

    private static HttpClient Client(IServiceProvider services)
    {
        var address = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Assert.StartsWith("http://127.0.0.1:", address, StringComparison.Ordinal);
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }
}
