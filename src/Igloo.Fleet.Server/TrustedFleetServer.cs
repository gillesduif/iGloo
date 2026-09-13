using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Igloo.Fleet.Server;

public interface IFleetOperatorAuthenticator
{
    string? Authenticate(HttpContext context);
}
public sealed class EngineeringOperatorAuthenticator(string token) : IFleetOperatorAuthenticator
{
    private readonly DevelopmentTokenAuthenticator _secret = new(token);
    public string? Authenticate(HttpContext context) => _secret.Authenticate(context) ? "engineering-operator" : null;
}

public static class TrustedFleetServer
{
    public static WebApplication Build(IPlanningStore store, FleetCertificateAuthority issuer,
        X509Certificate2 serverCertificate, string operatorSecret, int port = 5188, IPAddress? listenAddress = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            options.Limits.MaxRequestBodySize = 32 * 1024;
            options.Listen(listenAddress ?? IPAddress.Loopback, port, endpoint => endpoint.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.CheckCertificateRevocation = false; // Private CA: durable Fleet status is authoritative.
                https.ClientCertificateValidation = (certificate, _, _) => issuer.Validate(certificate);
            }));
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new PlanningService(store, TimeProvider.System));
        builder.Services.AddSingleton<IFleetOperatorAuthenticator>(new EngineeringOperatorAuthenticator(operatorSecret));
        var app = builder.Build();
        // Do not enable request body/header logging or developer exception pages on enrollment routes.
        app.Use(async (context, next) =>
        {
            var service = context.RequestServices.GetRequiredService<PlanningService>();
            try
            {
                var path = context.Request.Path;
                if (path.StartsWithSegments("/v2/operator", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.Connection.ClientCertificate is not null)
                        throw new PlanningException(FleetErrorCode.ApprovalNotAllowed);
                    var actor = context.RequestServices.GetRequiredService<IFleetOperatorAuthenticator>().Authenticate(context);
                    if (actor is null) throw new PlanningException(FleetErrorCode.EnrollmentFailure);
                    context.Items["actor"] = actor;
                }
                else if (path.StartsWithSegments("/v2/agent", StringComparison.OrdinalIgnoreCase))
                {
                    var certificate = await context.Connection.GetClientCertificateAsync().ConfigureAwait(false);
                    if (certificate is null || !issuer.Validate(certificate))
                        throw new PlanningException(FleetErrorCode.CertificateInvalid);
                    context.Items["identity"] = service.Authenticate(FleetCertificateAuthority.Hash(certificate)).Identity;
                }
                else if (path != "/v2/enroll" && path != "/v2/health")
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                await next(context).ConfigureAwait(false);
            }
            catch (PlanningException ex)
            {
                service.RecordDenial(ex.Code.ToString());
                context.Response.StatusCode = ex.Code is FleetErrorCode.CertificateInvalid or FleetErrorCode.CertificateRevoked
                    or FleetErrorCode.AgentDisabled or FleetErrorCode.EnrollmentFailure ? 401 :
                    ex.Code is FleetErrorCode.Conflict or FleetErrorCode.ApprovalStale ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new FleetError(ex.Code, ex.Code.ToString())).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or ArgumentException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new FleetError(FleetErrorCode.InvalidRequest, "Invalid request.")).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new FleetError(FleetErrorCode.PersistenceFailure, "Persistence unavailable.")).ConfigureAwait(false);
            }
        });
        app.MapGet("/v2/health", () => new { Protocol = PlanningProtocol.Version, SafetyBoundary = "PreparedOnly" });
        app.MapPost("/v2/enroll", (EnrollmentRequest request, PlanningService service) =>
        {
            if (request.Protocol != PlanningProtocol.Version) throw new PlanningException(FleetErrorCode.UnsupportedProtocol);
            return service.Enroll(request.Token, id => issuer.Issue(request.SigningRequestPem, id));
        });
        app.MapPost("/v2/operator/enrollment-tokens", (CreateEnrollmentTokenRequest request, HttpContext context, PlanningService service) =>
            service.CreateToken(request.LifetimeMinutes, Actor(context)));
        app.MapPost("/v2/operator/enrollment-tokens/{id:guid}/revoke", (Guid id, HttpContext context, PlanningService service) =>
        {
            service.RevokeToken(id, Actor(context));
            return Results.NoContent();
        });
        app.MapGet("/v2/operator/devices", (PlanningService service) => service.Devices());
        app.MapGet("/v2/operator/enrollment-tokens", (PlanningService service) => service.Tokens());
        app.MapPost("/v2/operator/devices/{id:guid}/trust", (Guid id, ChangeAgentTrustRequest request, HttpContext context, PlanningService service) =>
        {
            service.SetTrust(id, request.Status, Actor(context));
            return Results.NoContent();
        });
        app.MapPost("/v2/agent/heartbeat", (TrustedHeartbeat request, HttpContext context, PlanningService service) =>
        {
            service.Heartbeat(Identity(context), request);
            return Results.NoContent();
        });
        app.MapPost("/v2/agent/work/claim", (HttpContext context, PlanningService service) =>
            service.Claim(Identity(context)) is { } work ? Results.Ok(work) : Results.NoContent());
        app.MapPost("/v2/agent/results", (ReadOnlyWorkResult request, HttpContext context, PlanningService service) =>
            service.Submit(Identity(context), request));
        app.MapGet("/v2/agent/evidence", (HttpContext context, PlanningService service) =>
            service.Evidence(Identity(context).DeviceId));
        app.MapGet("/v2/operator/profiles", (PlanningService service) => service.Profiles());
        app.MapPost("/v2/operator/profiles", (MigrationProfileSpec request, HttpContext context, PlanningService service) =>
            service.SaveProfile(null, request, Actor(context)));
        app.MapPost("/v2/operator/profiles/{id:guid}/revisions", (Guid id, MigrationProfileSpec request, HttpContext context, PlanningService service) =>
            service.SaveProfile(id, request, Actor(context)));
        app.MapPost("/v2/operator/work", (RequestReadOnlyWork request, HttpContext context, PlanningService service) =>
            service.RequestWork(request, Actor(context)));
        app.MapGet("/v2/operator/work", (PlanningService service) => service.WorkItems());
        app.MapGet("/v2/operator/evidence", (PlanningService service) => service.Evidence());
        app.MapGet("/v2/operator/audit", (PlanningService service) => service.AuditEvents());
        app.MapGet("/v2/operator/approvals", (PlanningService service) => service.Approvals());
        app.MapPost("/v2/operator/approvals", (ApprovePlanRequest request, HttpContext context, PlanningService service) =>
            service.Approve(request, Actor(context)));
        app.MapPost("/v2/operator/approvals/{id:guid}/revoke", (Guid id, HttpContext context, PlanningService service) =>
        {
            service.RevokeApproval(id, Actor(context));
            return Results.NoContent();
        });
        app.MapPost("/v2/operator/plans", (PreparePlanRequest request, HttpContext context, PlanningService service) =>
            service.Prepare(request.ApprovalId, Actor(context)));
        app.MapGet("/v2/operator/plans", (PlanningService service) => service.Plans());
        return app;
    }

    private static DeviceIdentity Identity(HttpContext context) => (DeviceIdentity)context.Items["identity"]!;
    private static string Actor(HttpContext context) => (string)context.Items["actor"]!;
}
