using System.Net;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class AgentTests
{
    internal static PreflightReport Report => new()
    {
        IsUefi = true, SecureBootEnabled = true, TpmPresent = true, BitLocker = BitLockerState.NotEncrypted,
        TotalRamBytes = 8L * 1024 * 1024 * 1024, GpuVendor = "PRIVATE_GPU",
        GpuModel = "PRIVATE_MODEL", GpuDeviceId = "PRIVATE_ID",
        Disks = [new("PRIVATE_DEVICE", "PRIVATE_DISK", 100, 50, "GPT",
            [new(1, "NTFS", 100, "PRIVATE_LABEL", false, true)])],
        Findings = [new(FindingSeverity.Warning, "PRIVATE_CODE", "PRIVATE_PASSWORD", "PRIVATE_PATH")],
    };

    internal sealed class Checker : IPreflightChecker
    {
        public int Calls { get; private set; }
        public Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Report);
        }
    }

    [Fact]
    public async Task InvokesHeadlessInterfaceAndSerializesOnlyAllowlistedEvidence()
    {
        var checker = new Checker();
        var value = await new ReadOnlyAssessment(checker).RunAsync(new(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(1, checker.Calls);
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("PRIVATE", json, StringComparison.Ordinal);
        var copy = JsonSerializer.Deserialize<AssessmentResult>(json)!;
        Assert.Equal(value.AssessmentId, copy.AssessmentId);
        Assert.Equal(value.Checks.ToArray(), copy.Checks.ToArray());
        Assert.Equal(EligibilityStatus.NeedsReview, copy.Eligibility);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "AgentVersion", "AssessmentId", "Checks", "CompletedAt", "Eligibility",
            "Identity", "IglooVersion", "Inventory", "Outcome", "PreflightSchema", "Protocol", "StartedAt" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnknownProbeResultsRequireReview()
    {
        var report = Report with { Findings = [], BitLocker = BitLockerState.Unknown, TotalRamBytes = 0, Disks = [] };
        var value = ReadOnlyAssessment.Map(report, new(Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        Assert.Equal(EligibilityStatus.NeedsReview, value.Eligibility);
        Assert.Contains(value.Checks, c => c.Id == CheckId.BitLocker && c.Status == CheckStatus.Unknown);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    public async Task NetworkFailureNeverInvokesRemediation(int successfulRequests, int expectedAssessments)
    {
        var checker = new Checker();
        var identity = new DeviceIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var handler = new FailingHandler(successfulRequests, identity);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new FleetAssessmentClient(client, new ReadOnlyAssessment(checker)).RunAsync(identity));
        Assert.Equal(expectedAssessments, checker.Calls);
        Assert.All(handler.Paths, p => Assert.Contains(p, new[] { "/agents", "/heartbeats", "/assessments" }));
    }

    [Fact]
    public void DevelopmentIdentitySurvivesRestartAndRejectsCorruption()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-fleet-test-" + Guid.NewGuid());
        var path = Path.Join(directory, "identity.json");
        try
        {
            var first = new DevelopmentDeviceIdentityProvider(path).GetIdentity();
            Assert.Equal(first, new DevelopmentDeviceIdentityProvider(path).GetIdentity());
            File.WriteAllText(path, "{}");
            Assert.Throws<InvalidDataException>(() => new DevelopmentDeviceIdentityProvider(path).GetIdentity());
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private sealed class FailingHandler(int successfulRequests, DeviceIdentity identity) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (Paths.Count > successfulRequests)
                throw new HttpRequestException("PRIVATE_NETWORK_DETAIL");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new AgentRegistrationResponse(FleetProtocolVersion.Current, identity)),
            });
        }
    }

    [Fact]
    public async Task LocalFailureProducesSanitizedEvidence()
    {
        var value = await new ReadOnlyAssessment(new ThrowingChecker()).RunAsync(new(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(AssessmentOutcome.LocalExecutionFailed, value.Outcome);
        Assert.Equal(EligibilityStatus.NeedsReview, value.Eligibility);
        Assert.Empty(value.Checks);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeSuccessfulEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReadOnlyAssessment(new Checker()).RunAsync(new(Guid.NewGuid(), Guid.NewGuid()), cancellation.Token));
    }

    private sealed class ThrowingChecker : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            throw new IOException("PRIVATE_PATH and PRIVATE_PASSWORD");
    }
}
