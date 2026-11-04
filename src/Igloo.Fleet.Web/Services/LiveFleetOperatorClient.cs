using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using Igloo.Fleet.Contracts;
using Microsoft.Extensions.Options;

namespace Igloo.Fleet.Web.Services;

/// <summary>Talks to the trusted Fleet Server over HTTPS with the separate operator credential.</summary>
internal sealed class LiveFleetOperatorClient(HttpClient http, IOptions<FleetServerOptions> options) : IFleetOperatorClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly bool _configured = options.Value.ResolveUri() is not null
        && options.Value.ResolveCaCertificatePath() is not null
        && FleetServerOptions.HasOperatorSecret();

    public bool IsSampleData => false;

    public static void ConfigureClient(HttpClient client, FleetServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        client.BaseAddress = options.ResolveUri() ?? new Uri("https://localhost:5188/");
        client.Timeout = TimeSpan.FromSeconds(15);
        var secret = Environment.GetEnvironmentVariable(FleetServerOptions.OperatorSecretVariable);
        if (!string.IsNullOrEmpty(secret))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    public Task<OperatorResult<ServerHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ServerHealth>("v2/health", cancellationToken);

    public Task<OperatorResult<IReadOnlyList<TrustedDeviceView>>> GetEnrollmentsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<TrustedDeviceView>("v2/operator/devices", cancellationToken);

    public Task<OperatorResult> ChangeTrustAsync(Guid deviceId, AgentTrustStatus status, CancellationToken cancellationToken = default) =>
        PostAsync($"v2/operator/devices/{deviceId:D}/trust", new ChangeAgentTrustRequest(status), cancellationToken);

    public Task<OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<EnrollmentTokenInfo>("v2/operator/enrollment-tokens", cancellationToken);

    public Task<OperatorResult<EnrollmentTokenCreated>> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default) =>
        PostAsync<CreateEnrollmentTokenRequest, EnrollmentTokenCreated>("v2/operator/enrollment-tokens", new(lifetimeMinutes), cancellationToken);

    public Task<OperatorResult> RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        PostAsync<object?>($"v2/operator/enrollment-tokens/{tokenId:D}/revoke", null, cancellationToken);

    public Task<OperatorResult<IReadOnlyList<MigrationProfileRevision>>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<MigrationProfileRevision>("v2/operator/profiles", cancellationToken);

    public Task<OperatorResult<MigrationProfileRevision>> CreateProfileAsync(MigrationProfileSpec spec, CancellationToken cancellationToken = default) =>
        PostAsync<MigrationProfileSpec, MigrationProfileRevision>("v2/operator/profiles", spec, cancellationToken);

    public Task<OperatorResult<MigrationProfileRevision>> AddProfileRevisionAsync(Guid profileId, MigrationProfileSpec spec, CancellationToken cancellationToken = default) =>
        PostAsync<MigrationProfileSpec, MigrationProfileRevision>($"v2/operator/profiles/{profileId:D}/revisions", spec, cancellationToken);

    public Task<OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>> GetWorkItemsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<ReadOnlyWorkItem>("v2/operator/work", cancellationToken);

    public Task<OperatorResult<ReadOnlyWorkItem>> RequestWorkAsync(RequestReadOnlyWork request, CancellationToken cancellationToken = default) =>
        PostAsync<RequestReadOnlyWork, ReadOnlyWorkItem>("v2/operator/work", request, cancellationToken);

    public Task<OperatorResult<IReadOnlyList<DryRunEvidence>>> GetEvidenceAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<DryRunEvidence>("v2/operator/evidence", cancellationToken);

    public Task<OperatorResult<IReadOnlyList<PlanApproval>>> GetApprovalsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<PlanApproval>("v2/operator/approvals", cancellationToken);

    public Task<OperatorResult<PlanApproval>> ApproveAsync(ApprovePlanRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ApprovePlanRequest, PlanApproval>("v2/operator/approvals", request, cancellationToken);

    public Task<OperatorResult> RevokeApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        PostAsync<object?>($"v2/operator/approvals/{approvalId:D}/revoke", null, cancellationToken);

    public Task<OperatorResult<IReadOnlyList<PreparedMigrationPlan>>> GetPlansAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<PreparedMigrationPlan>("v2/operator/plans", cancellationToken);

    public Task<OperatorResult<PreparedMigrationPlan>> PrepareAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        PostAsync<PreparePlanRequest, PreparedMigrationPlan>("v2/operator/plans", new(approvalId), cancellationToken);

    public Task<OperatorResult<IReadOnlyList<FleetAuditEvent>>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<FleetAuditEvent>("v2/operator/audit", cancellationToken);

    private async Task<OperatorResult<IReadOnlyList<T>>> GetListAsync<T>(string route, CancellationToken cancellationToken)
    {
        var result = await GetAsync<T[]>(route, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? OperatorResult<IReadOnlyList<T>>.Success(result.Value ?? [])
            : OperatorResult<IReadOnlyList<T>>.Failed(result.Failure!);
    }

    private async Task<OperatorResult<T>> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        if (!_configured)
        {
            return OperatorResult<T>.Failed(OperatorFailureKind.NotConfigured);
        }

        try
        {
            using var response = await http.GetAsync(new Uri(route, UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperatorResult<T>.Failed(await ClassifyAsync(response, cancellationToken).ConfigureAwait(false));
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            return value is null
                ? OperatorResult<T>.Failed(OperatorFailureKind.ServerError, null, (int)response.StatusCode)
                : OperatorResult<T>.Success(value);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return OperatorResult<T>.Failed(ClassifyTransport(exception));
        }
    }

    private async Task<OperatorResult<TResponse>> PostAsync<TRequest, TResponse>(string route, TRequest body, CancellationToken cancellationToken)
    {
        if (!_configured)
        {
            return OperatorResult<TResponse>.Failed(OperatorFailureKind.NotConfigured);
        }

        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(route, UriKind.Relative), body, Json, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OperatorResult<TResponse>.Failed(await ClassifyAsync(response, cancellationToken).ConfigureAwait(false));
            }

            var value = await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken).ConfigureAwait(false);
            return value is null
                ? OperatorResult<TResponse>.Failed(OperatorFailureKind.ServerError, null, (int)response.StatusCode)
                : OperatorResult<TResponse>.Success(value);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return OperatorResult<TResponse>.Failed(ClassifyTransport(exception));
        }
    }

    private async Task<OperatorResult> PostAsync<TRequest>(string route, TRequest body, CancellationToken cancellationToken)
    {
        if (!_configured)
        {
            return OperatorResult.Failed(OperatorFailureKind.NotConfigured);
        }

        try
        {
            using var response = body is null
                ? await http.PostAsync(new Uri(route, UriKind.Relative), content: null, cancellationToken).ConfigureAwait(false)
                : await http.PostAsJsonAsync(new Uri(route, UriKind.Relative), body, Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? OperatorResult.Success
                : new OperatorResult(await ClassifyAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return new OperatorResult(ClassifyTransport(exception));
        }
    }

    private static async Task<OperatorFailure> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        FleetError? error = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                error = await response.Content.ReadFromJsonAsync<FleetError>(Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                error = null;
            }
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new OperatorFailure(OperatorFailureKind.NotAuthorized, error, status),
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound => new OperatorFailure(OperatorFailureKind.Rejected, error, status),
            _ => new OperatorFailure(OperatorFailureKind.ServerError, error, status),
        };
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or AuthenticationException;

    private static OperatorFailure ClassifyTransport(Exception exception) =>
        exception is AuthenticationException || exception.InnerException is AuthenticationException
            ? new OperatorFailure(OperatorFailureKind.CertificateNotTrusted)
            : new OperatorFailure(OperatorFailureKind.Unreachable);
}
