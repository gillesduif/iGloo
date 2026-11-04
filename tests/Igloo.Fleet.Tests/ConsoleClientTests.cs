using Igloo.Fleet.Contracts;
using Igloo.Fleet.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Igloo.Fleet.Tests;

/// <summary>Console-side services: the sample client follows the Server's operator rules, the audit log never sees a token.</summary>
public sealed class ConsoleClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "igloo-console-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SampleDataIsInternallyConsistent()
    {
        var client = new SampleFleetOperatorClient(new SampleFleetData());
        var enrollments = (await client.GetEnrollmentsAsync()).Value!;
        var work = (await client.GetWorkItemsAsync()).Value!;
        var evidence = (await client.GetEvidenceAsync()).Value!;
        var approvals = (await client.GetApprovalsAsync()).Value!;
        var plans = (await client.GetPlansAsync()).Value!;

        Assert.All(work, w => Assert.Contains(enrollments, e => e.Identity == w.Identity));
        Assert.All(evidence, e => Assert.Contains(work, w => w.WorkItemId == e.DryRunId && w.Identity == e.Result.Assessment.Identity));
        Assert.All(evidence, e => Assert.Equal(e.DryRunId, e.Result.Assessment.AssessmentId));
        Assert.All(approvals, a => Assert.Contains(evidence, e => e.DryRunId == a.DryRunId && e.EvidenceHash == a.EvidenceHash && e.Decision.DecisionId == a.DecisionId));
        Assert.All(plans, p => Assert.Contains(approvals, a => a.ApprovalId == p.ApprovalId && a.EvidenceHash == p.EvidenceHash));
        // An assessment carries no dry-run facts, so its decision can only be Blocked or Not determined.
        Assert.All(evidence.Where(static e => e.Result.Planning is null), e => Assert.True(e.Decision.Status is PlanningEligibility.Unknown or PlanningEligibility.Blocked));
        Assert.Equal(enrollments.Select(static e => e.Identity.DeviceId).Distinct().Count(), enrollments.Count);
    }

    [Fact]
    public async Task ApprovalFollowsTheServerRules()
    {
        var client = new SampleFleetOperatorClient(new SampleFleetData());
        var evidence = (await client.GetEvidenceAsync()).Value!;

        var blocked = evidence.First(static e => e.Decision.Status == PlanningEligibility.Blocked && e.Result.Planning is not null);
        var rejected = await client.ApproveAsync(Request(blocked, null));
        Assert.Equal(FleetErrorCode.EligibilityBlocked, rejected.Failure!.Error!.Code);

        var needsReview = evidence.First(static e => e.Decision.Status == PlanningEligibility.NeedsReview);
        var withoutReason = await client.ApproveAsync(Request(needsReview, null));
        Assert.Equal(FleetErrorCode.ApprovalNotAllowed, withoutReason.Failure!.Error!.Code);

        var approvals = (await client.GetApprovalsAsync()).Value!;
        var eligible = evidence.First(e => e.Decision.Status == PlanningEligibility.Eligible && !approvals.Any(a => a.DryRunId == e.DryRunId));
        var approved = await client.ApproveAsync(Request(eligible, null));
        Assert.True(approved.Succeeded, approved.Failure?.Error?.Code.ToString());
        Assert.Equal(ApprovalState.Approved, approved.Value!.Status);

        var prepared = await client.PrepareAsync(approved.Value.ApprovalId);
        Assert.True(prepared.Succeeded);
        Assert.Equal(PreparedPlanState.Prepared, prepared.Value!.Status);
        Assert.Contains("Non-executable", prepared.Value.SafetyBoundary, StringComparison.Ordinal);

        Assert.True((await client.RevokeApprovalAsync(approved.Value.ApprovalId)).Succeeded);
        var plans = (await client.GetPlansAsync()).Value!;
        Assert.Equal(PreparedPlanState.Invalidated, plans.Single(p => p.PlanId == prepared.Value.PlanId).Status);
    }

    [Fact]
    public async Task TokensAreSingleUseAndBounded()
    {
        var client = new SampleFleetOperatorClient(new SampleFleetData());
        Assert.False((await client.CreateEnrollmentTokenAsync(0)).Succeeded);
        Assert.False((await client.CreateEnrollmentTokenAsync(61)).Succeeded);
        var created = await client.CreateEnrollmentTokenAsync(10);
        Assert.True(created.Succeeded);
        Assert.Equal(64, created.Value!.Token.Length);
        Assert.True((await client.RevokeEnrollmentTokenAsync(created.Value.TokenId)).Succeeded);
        var tokens = (await client.GetEnrollmentTokensAsync()).Value!;
        Assert.True(tokens.Single(t => t.TokenId == created.Value.TokenId).Revoked);
    }

    [Fact]
    public async Task AuditingClientRecordsTheAccountButNeverTheToken()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FleetAuthentication:DataDirectory"] = _directory }).Build();
        var log = new ConsoleAuditLog(configuration);
        var session = new ConsoleSession(new FixedAuthenticationState("console.operator"));
        var client = new AuditingFleetOperatorClient(new SampleFleetOperatorClient(new SampleFleetData()), log, session);

        var created = await client.CreateEnrollmentTokenAsync(5);
        Assert.True(created.Succeeded);

        var entries = log.ReadRecent(10);
        var entry = Assert.Single(entries);
        Assert.Equal("console.operator", entry.Account);
        Assert.Equal("CreateEnrollmentToken", entry.Action);
        Assert.Equal(created.Value!.TokenId.ToString("D"), entry.Target);
        Assert.DoesNotContain(created.Value.Token, await File.ReadAllTextAsync(Path.Combine(_directory, "console-audit.jsonl")), StringComparison.Ordinal);
        Assert.Equal(entry, log.FindByTarget(created.Value.TokenId.ToString("D")));
    }

    [Fact]
    public async Task LiveClientReportsNotConfiguredWithoutASecretOrRoot()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FleetServerOptions { Uri = "https://localhost:5188/", CaCertificatePath = Path.Combine(_directory, "missing.pem") });
        using var http = new HttpClient { BaseAddress = new Uri("https://localhost:5188/") };
        var client = new LiveFleetOperatorClient(http, options);
        var result = await client.GetEnrollmentsAsync();
        Assert.Equal(OperatorFailureKind.NotConfigured, result.Failure!.Kind);
    }

    private static ApprovePlanRequest Request(DryRunEvidence evidence, string? reason) =>
        new(evidence.DryRunId, evidence.EvidenceHash, evidence.Result.ProfileRevisionId!.Value, evidence.Decision.DecisionId, reason);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedAuthenticationState(string username) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "Console operator"),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.Empty.ToString("D")),
                    new System.Security.Claims.Claim(ConsoleSession.UsernameClaim, username),
                ], "test");
            return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity)));
        }
    }
}
