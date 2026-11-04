using Bunit;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Web;
using Igloo.Fleet.Web.Components.Common;
using Igloo.Fleet.Web.Components.Pages;
using Igloo.Fleet.Web.Components.Pages.Enrollments;
using Igloo.Fleet.Web.Components.Pages.Modules;
using Microsoft.AspNetCore.Components;
using Igloo.Fleet.Web.Presentation;
using Igloo.Fleet.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Igloo.Fleet.Tests;

/// <summary>bUnit coverage of the shared components and the page states that depend on data.</summary>
public sealed class ConsoleComponentTests : BunitContext
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "igloo-console-component-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SampleFleetData _data = new();

    public ConsoleComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FleetAuthentication:DataDirectory"] = _directory }).Build();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        Services.AddFluentUIComponents();
        Services.AddSingleton<IConfiguration>(configuration);
        Services.AddSingleton(new ConsoleAuditLog(configuration));
        Services.AddSingleton(new ConsolePreferencesStore(configuration));
        Services.AddSingleton(Options.Create(new ConsoleOptions()));
        Services.AddSingleton(Options.Create(new FleetServerOptions()));
        Services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        Services.AddScoped<ConsoleShell>();
        Services.AddScoped<ConsoleSession>();
        AddAuthorization().SetAuthorized("Console operator");
    }

    [Fact]
    public void StatusBadgeShowsTextAndMeaning()
    {
        var cut = Render<StatusBadge>(parameters => parameters.Add(p => p.Presentation, StatusCatalog.Trust(AgentTrustStatus.Revoked)));
        Assert.Contains("Revoked", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Permanently refused", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<svg", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureStateNamesTheCauseAndOffersSettingsExceptForRejections()
    {
        var unreachable = Render<OperatorFailureState>(parameters => parameters.Add(p => p.Failure, new OperatorFailure(OperatorFailureKind.Unreachable)));
        Assert.Contains("Fleet Server is unreachable", unreachable.Markup, StringComparison.Ordinal);
        Assert.Contains("/settings/server", unreachable.Markup, StringComparison.Ordinal);

        var rejected = Render<OperatorFailureState>(parameters => parameters.Add(p => p.Failure,
            new OperatorFailure(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ApprovalStale, "ApprovalStale"), 409)));
        Assert.Contains("ApprovalStale", rejected.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/server", rejected.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewCountsOnlyApprovableDryRunsAsAwaitingApproval()
    {
        Services.AddScoped<IFleetOperatorClient>(_ => new SampleFleetOperatorClient(_data));
        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Assert.Contains("Evidence awaiting approval", cut.Markup, StringComparison.Ordinal));

        var evidence = _data.Read(static d => d.Evidence.Values.ToArray());
        var approvals = _data.Read(static d => d.Approvals.Values.ToArray());
        var expected = evidence.Count(e => e.Result.Planning is not null
            && e.Decision.Status is PlanningEligibility.Eligible or PlanningEligibility.NeedsReview
            && !approvals.Any(a => a.DryRunId == e.DryRunId && a.Status == ApprovalState.Approved));
        var tile = cut.FindAll(".tile").Single(t => t.TextContent.Contains("Evidence awaiting approval", StringComparison.Ordinal));
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.CurrentCulture), tile.QuerySelector(".tile-value")!.TextContent.Trim());
        Assert.DoesNotContain(cut.FindAll(".tile-label"), static t => t.TextContent.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnrollmentListShowsFailureEmptyAndRowsStates()
    {
        var failing = new StubClient(OperatorResult<IReadOnlyList<TrustedDeviceView>>.Failed(OperatorFailureKind.CertificateNotTrusted));
        Services.AddScoped<IFleetOperatorClient>(_ => failing);
        var failed = Render<EnrollmentList>();
        failed.WaitForAssertion(() => Assert.Contains("Server certificate is not trusted", failed.Markup, StringComparison.Ordinal));

        failing.Enrollments = OperatorResult<IReadOnlyList<TrustedDeviceView>>.Success([]);
        var empty = Render<EnrollmentList>();
        empty.WaitForAssertion(() => Assert.Contains("No enrollments", empty.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Device name", empty.Markup, StringComparison.Ordinal);

        failing.Enrollments = OperatorResult<IReadOnlyList<TrustedDeviceView>>.Success(_data.Read(static d => d.Enrollments.Values.ToArray()));
        var rows = Render<EnrollmentList>();
        rows.WaitForAssertion(() => Assert.Contains("8 items", rows.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("role=\"link\"", rows.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrollmentDetailsOffersNoTrustChangeOnceRevoked()
    {
        Services.AddScoped<IFleetOperatorClient>(_ => new SampleFleetOperatorClient(_data));
        var revoked = _data.Read(static d => d.Enrollments.Values.First(e => e.Status == AgentTrustStatus.Revoked));
        var cut = Render<EnrollmentDetails>(parameters => parameters.Add(p => p.DeviceId, revoked.Identity.DeviceId));
        cut.WaitForAssertion(() => Assert.Contains("trust status can't be changed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Change trust status", cut.Markup, StringComparison.Ordinal);

        var active = _data.Read(static d => d.Enrollments.Values.First(e => e.Status == AgentTrustStatus.Active));
        var activeCut = Render<EnrollmentDetails>(parameters => parameters.Add(p => p.DeviceId, active.Identity.DeviceId));
        activeCut.WaitForAssertion(() => Assert.Contains("Change trust status", activeCut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void EvidencePageOffersApprovalOnlyForApprovableDryRuns()
    {
        Services.AddScoped<IFleetOperatorClient>(_ => new SampleFleetOperatorClient(_data));
        var evidence = _data.Read(static d => d.Evidence.Values.ToArray());
        var approvals = _data.Read(static d => d.Approvals.Values.ToArray());

        var assessment = evidence.First(static e => e.Result.Planning is null);
        var assessmentCut = Render<Igloo.Fleet.Web.Components.Pages.Planning.EvidenceDetails>(p => p.Add(x => x.DryRunId, assessment.DryRunId));
        assessmentCut.WaitForAssertion(() => Assert.Contains("Assessment outcome", assessmentCut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(assessmentCut.FindAll("fluent-button"), static b => b.TextContent.Trim() == "Approve");
        Assert.DoesNotContain("Missing evidence", assessmentCut.Markup.Split("Reasons")[0], StringComparison.Ordinal);

        var blocked = evidence.First(static e => e.Result.Planning is not null && e.Decision.Status == PlanningEligibility.Blocked);
        var blockedCut = Render<Igloo.Fleet.Web.Components.Pages.Planning.EvidenceDetails>(p => p.Add(x => x.DryRunId, blocked.DryRunId));
        blockedCut.WaitForAssertion(() => Assert.Contains("Blocked evidence can't be approved", blockedCut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(blockedCut.FindAll("fluent-button"), static b => b.TextContent.Trim() == "Approve");

        var eligible = evidence.First(e => e.Result.Planning is not null && e.Decision.Status == PlanningEligibility.Eligible && !approvals.Any(a => a.DryRunId == e.DryRunId));
        var eligibleCut = Render<Igloo.Fleet.Web.Components.Pages.Planning.EvidenceDetails>(p => p.Add(x => x.DryRunId, eligible.DryRunId));
        eligibleCut.WaitForAssertion(() => Assert.Contains("Can be approved for planning. Approval doesn't authorize a migration.", eligibleCut.Markup, StringComparison.Ordinal));
        Assert.Contains(eligibleCut.FindAll("fluent-button"), static b => b.TextContent.Trim() == "Approve");
    }

    [Fact]
    public void ModulePageListsSampleRowsWithStatusFilterAndDetailsLinks()
    {
        Services.AddSingleton<IModuleDataSource>(new ModuleSampleStore(_data));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/software/packages");
        var cut = Render<ModulePage>(parameters => parameters.Add(p => p.Slug, "packages"));
        Assert.Contains("<h1", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Packages", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("libssl3", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.NotEmpty(cut.FindAll("a[href^='/software/packages/']"));
        Assert.NotEmpty(cut.FindAll("fluent-dropdown, fluent-select"));
        Assert.DoesNotContain("coming soon", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModuleDetailsPageShowsEveryFieldOfTheRecord()
    {
        var store = new ModuleSampleStore(_data);
        Services.AddSingleton<IModuleDataSource>(store);
        var page = ModuleCatalog.Find("configuration", "policies")!;
        var row = store.Rows(page)![0];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/configuration/policies/{row.Id:D}");
        var cut = Render<ModulePage>(parameters => parameters.Add(p => p.Slug, "policies").Add(p => p.Id, row.Id));
        Assert.Contains("Properties", cut.Markup, StringComparison.Ordinal);
        Assert.Contains((string)row["Name"]!, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Record ID", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePageStatesWhenTheServerProvidesNoData()
    {
        Services.AddSingleton<IModuleDataSource>(new UnavailableModuleDataSource());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cohorts");
        var cut = Render<ModulePage>();
        Assert.Contains("doesn't provide data", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("fluent-data-grid"));
        Assert.DoesNotContain("Create cohort", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleSampleStoreAddsCreatedRecordsFirst()
    {
        var store = new ModuleSampleStore(_data);
        var page = ModuleCatalog.Find("cohorts", "")!;
        var before = store.Rows(page)!.Count;
        var created = store.Add(page, new Dictionary<string, string> { ["Name"] = "Branch office Antwerp", ["Rule"] = "trust = Active" }, "tester");
        var rows = store.Rows(page)!;
        Assert.Equal(before + 1, rows.Count);
        Assert.Equal(created.Id, rows[0].Id);
        Assert.Equal("Draft", rows[0]["Status"]);
        Assert.Equal("tester", rows[0]["Owner"]);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Igloo.Fleet.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubClient(OperatorResult<IReadOnlyList<TrustedDeviceView>> enrollments) : IFleetOperatorClient
    {
        public OperatorResult<IReadOnlyList<TrustedDeviceView>> Enrollments { get; set; } = enrollments;
        public bool IsSampleData => false;
        public Task<OperatorResult<IReadOnlyList<TrustedDeviceView>>> GetEnrollmentsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enrollments);
        public Task<OperatorResult<ServerHealth>> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<ServerHealth>.Failed(OperatorFailureKind.Unreachable));
        public Task<OperatorResult> ChangeTrustAsync(Guid deviceId, AgentTrustStatus status, CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult.Success);
        public Task<OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>.Success([]));
        public Task<OperatorResult<EnrollmentTokenCreated>> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult> RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult.Success);
        public Task<OperatorResult<IReadOnlyList<MigrationProfileRevision>>> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<MigrationProfileRevision>>.Success([]));
        public Task<OperatorResult<MigrationProfileRevision>> CreateProfileAsync(MigrationProfileSpec spec, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult<MigrationProfileRevision>> AddProfileRevisionAsync(Guid profileId, MigrationProfileSpec spec, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>> GetWorkItemsAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>.Success([]));
        public Task<OperatorResult<ReadOnlyWorkItem>> RequestWorkAsync(RequestReadOnlyWork request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult<IReadOnlyList<DryRunEvidence>>> GetEvidenceAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<DryRunEvidence>>.Success([]));
        public Task<OperatorResult<IReadOnlyList<PlanApproval>>> GetApprovalsAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<PlanApproval>>.Success([]));
        public Task<OperatorResult<PlanApproval>> ApproveAsync(ApprovePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult> RevokeApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult.Success);
        public Task<OperatorResult<IReadOnlyList<PreparedMigrationPlan>>> GetPlansAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<PreparedMigrationPlan>>.Success([]));
        public Task<OperatorResult<PreparedMigrationPlan>> PrepareAsync(Guid approvalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorResult<IReadOnlyList<FleetAuditEvent>>> GetAuditAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperatorResult<IReadOnlyList<FleetAuditEvent>>.Success([]));
    }
}
