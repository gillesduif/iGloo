using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class PlannerAndSpoolTests
{
    [Fact]
    public async Task RealPluginCompatibilityRunsHeadlesslyAndDoesNotLeakLocalDetails()
    {
        using var fixture = new PlanningFixture();
        var identity = PlanningTests.Device(fixture);
        var (work, _) = PlanningTests.Work(fixture, identity);
        var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        await registry.LoadAsync(Path.Join(Phase1SafetyTests.RepositoryRoot(), "distros"));
        var checker = new AgentTests.Checker();
        var result = await new ReadOnlyPlanner(checker, registry).RunAsync(work);
        Assert.Equal(1, checker.Calls);
        Assert.True(result.Planning!.TargetSupported);
        Assert.Equal(64, result.Planning.PluginHash!.Length);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var missing = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        var unsupported = await new ReadOnlyPlanner(checker, missing).RunAsync(work);
        Assert.False(unsupported.Planning!.TargetSupported);
        Assert.Contains(PlanningReason.UnsupportedDistro, unsupported.Planning.Reasons);
    }

    [Fact]
    public async Task FailedReadIsSanitizedUnknownEvidenceAndCannotBeApproved()
    {
        using var fixture = new PlanningFixture();
        var identity = PlanningTests.Device(fixture);
        var (work, _) = PlanningTests.Work(fixture, identity);
        var planner = new ReadOnlyPlanner(new FailingChecker(), new DistroRegistry(NullLogger<DistroRegistry>.Instance));
        var result = await planner.RunAsync(work);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var stored = fixture.Service.Submit(identity, result);
        Assert.Equal(PlanningEligibility.Unknown, stored.Decision.Status);
        Assert.Throws<Igloo.Fleet.Domain.PlanningException>(() => PlanningTests.Approve(fixture, stored, "Review"));
    }

    [Fact]
    public async Task RestartedSpoolRetriesBeforeClaimingNewWork()
    {
        using var fixture = new PlanningFixture();
        var identity = PlanningTests.Device(fixture);
        var (_, result) = PlanningTests.Work(fixture, identity);
        var directory = Path.Join(Path.GetDirectoryName(fixture.Path), "spool");
        new ResultSpool(directory).Save(result);
        var checker = new AgentTests.Checker();
        using var handler = new RetryHandler(directory, result.WorkItemId);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        await new TrustedAgentWorkflow(client, new ReadOnlyPlanner(checker,
            new DistroRegistry(NullLogger<DistroRegistry>.Instance)), new ResultSpool(directory)).RunOnceAsync(identity);
        Assert.Equal(2, handler.Submissions);
        Assert.Equal(0, checker.Calls);
        Assert.Empty(new ResultSpool(directory).Pending());
    }

    [Fact]
    public void ExpiredCertificateAndMalformedProofOfPossessionAreRejected()
    {
        using var root = FleetCertificateAuthority.CreateRoot();
        using var issuer = new FleetCertificateAuthority(root);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=expired", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var expired = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-1), RandomNumberGenerator.GetBytes(16));
        Assert.False(issuer.Validate(expired));
        Assert.ThrowsAny<Exception>(() => issuer.Issue("invalid-csr", new(Guid.NewGuid(), Guid.NewGuid())));
    }

    private sealed class FailingChecker : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken ct = default) => throw new IOException("PRIVATE_PATH");
    }

    private sealed class RetryHandler(string directory, Guid workId) : HttpMessageHandler
    {
        public int Submissions { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/results", StringComparison.Ordinal))
            {
                Assert.True(File.Exists(Path.Join(directory, workId + ".json")));
                if (++Submissions == 1) throw new HttpRequestException("Temporary connection failure");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
