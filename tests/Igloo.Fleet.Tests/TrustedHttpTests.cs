using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Igloo.Fleet.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class TrustedHttpTests
{
    private const string OperatorSecret = "separate-engineering-operator-secret-for-tests";

    [Fact]
    public async Task TlsEnrollmentAuthorizationAndPlanningWorkEndToEnd()
    {
        using var fixture = new PlanningFixture();
        using var root = FleetCertificateAuthority.CreateRoot();
        using var issuer = new FleetCertificateAuthority(root);
        using var serverCertificate = issuer.CreateServerCertificate();
        await using var app = TrustedFleetServer.Build(new SqlitePlanningStore(fixture.Path), issuer, serverCertificate, OperatorSecret, 0);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Assert.Equal("https", address.Scheme);
        using var operatorHandler = FleetTls.CreateHandler(root);
        using var op = new HttpClient(operatorHandler) { BaseAddress = address };
        op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", OperatorSecret);
        using var created = await op.PostAsJsonAsync("v2/operator/enrollment-tokens", new CreateEnrollmentTokenRequest(10));
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<EnrollmentTokenCreated>())!;
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=hostname-that-can-change", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var enrolledResponse = await op.PostAsJsonAsync("v2/enroll",
            new EnrollmentRequest(PlanningProtocol.Version, token.Token, csr.CreateSigningRequestPem()));
        enrolledResponse.EnsureSuccessStatusCode();
        var enrolled = (await enrolledResponse.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
        var identityDirectory = System.IO.Path.Join(System.IO.Path.GetDirectoryName(fixture.Path), "identity");
        var identityStore = new EnrolledIdentityStore(identityDirectory);
        identityStore.Save(enrolled, key);
        var (metadata, identityCertificate) = new EnrolledIdentityStore(identityDirectory).Load();
        using (identityCertificate)
        using (var handler = FleetTls.CreateHandler(root, identityCertificate))
        using (var agent = new HttpClient(handler) { BaseAddress = address })
        {
            Assert.Equal(enrolled.Identity, metadata.Identity);
            using var heartbeat = await agent.PostAsJsonAsync("v2/agent/heartbeat",
                new TrustedHeartbeat(PlanningProtocol.Version, "0.1.0", ReadOnlyAssessment.IglooVersion, [.. Enum.GetValues<PlanningCapability>()]));
            heartbeat.EnsureSuccessStatusCode();
            using var forbidden = await agent.GetAsync(new Uri("v2/operator/devices", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
            using var noCertificate = await op.GetAsync(new Uri("v2/agent/evidence", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, noCertificate.StatusCode);
            using var profileResponse = await op.PostAsJsonAsync("v2/operator/profiles",
                new MigrationProfileSpec("Debian", "debian", 20L << 30, false));
            var profile = (await profileResponse.Content.ReadFromJsonAsync<MigrationProfileRevision>())!;
            using var workResponse = await op.PostAsJsonAsync("v2/operator/work",
                new RequestReadOnlyWork(enrolled.Identity.DeviceId, ReadOnlyWorkType.RunMigrationDryRun, profile.RevisionId, 30));
            workResponse.EnsureSuccessStatusCode();
            using var claim = await agent.PostAsync(new Uri("v2/agent/work/claim", UriKind.Relative), null);
            var work = (await claim.Content.ReadFromJsonAsync<ReadOnlyWorkItem>())!;
            var assessment = ReadOnlyAssessment.Map(AgentTests.Report with { Findings = [] }, enrolled.Identity, DateTimeOffset.UtcNow)
                with { AssessmentId = work.WorkItemId };
            var result = new ReadOnlyWorkResult(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value,
                work.CorrelationId, profile.RevisionId, assessment, new(true, true, 100L << 30, 20L << 30, new string('B', 64), []));
            using var submitted = await agent.PostAsJsonAsync("v2/agent/results", result);
            submitted.EnsureSuccessStatusCode();
            var evidence = (await submitted.Content.ReadFromJsonAsync<DryRunEvidence>())!;
            using var approved = await op.PostAsJsonAsync("v2/operator/approvals",
                new ApprovePlanRequest(evidence.DryRunId, evidence.EvidenceHash, profile.RevisionId, evidence.Decision.DecisionId, null));
            approved.EnsureSuccessStatusCode();
            var approval = (await approved.Content.ReadFromJsonAsync<PlanApproval>())!;
            using var prepared = await op.PostAsJsonAsync("v2/operator/plans", new PreparePlanRequest(approval.ApprovalId));
            prepared.EnsureSuccessStatusCode();
            Assert.Equal(PreparedPlanState.Prepared, (await prepared.Content.ReadFromJsonAsync<PreparedMigrationPlan>())!.Status);
            using var revoked = await op.PostAsJsonAsync("v2/operator/devices/" + enrolled.Identity.DeviceId + "/trust",
                new ChangeAgentTrustRequest(AgentTrustStatus.Revoked));
            revoked.EnsureSuccessStatusCode();
            using var denied = await agent.GetAsync(new Uri("v2/agent/evidence", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var command = await op.PostAsync(new Uri("v2/operator/run-command", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NotFound, command.StatusCode);
        }
    }

    [Fact]
    public async Task WrongServerTrustAndUntrustedClientCertificateAreRejected()
    {
        using var fixture = new PlanningFixture();
        using var root = FleetCertificateAuthority.CreateRoot();
        using var wrongRoot = FleetCertificateAuthority.CreateRoot();
        using var issuer = new FleetCertificateAuthority(root);
        using var serverCertificate = issuer.CreateServerCertificate();
        await using var app = TrustedFleetServer.Build(new SqlitePlanningStore(fixture.Path), issuer, serverCertificate, OperatorSecret, 0);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var wrongHandler = FleetTls.CreateHandler(wrongRoot);
        using var wrongClient = new HttpClient(wrongHandler) { BaseAddress = address };
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongClient.GetAsync(new Uri("v2/health", UriKind.Relative)));
        Assert.False(issuer.Validate(wrongRoot));
        Assert.False(issuer.Validate(root)); // CA certificates cannot authenticate as Agents.
    }
}
