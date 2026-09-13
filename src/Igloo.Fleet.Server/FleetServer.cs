using System.Net;
using System.Text.Json.Serialization;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace Igloo.Fleet.Server;

public static class FleetServer
{
    /// <summary>Phase 0 always binds IPv4 loopback. Port zero is available to integration tests.</summary>
    public static WebApplication Build(string token, int port = 5187)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("Custom Kestrel endpoints are disabled in Phase 0.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Do not allow configuration reload to add network listeners after startup.
            options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 16 * 1024;
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        builder.Services.AddSingleton<IFleetAgentAuthenticator>(new DevelopmentTokenAuthenticator(token));
        builder.Services.AddSingleton<InMemoryFleetStore>();
        builder.Services.AddSingleton<IDeviceRepository>(s => s.GetRequiredService<InMemoryFleetStore>());
        builder.Services.AddSingleton<IAssessmentRepository>(s => s.GetRequiredService<InMemoryFleetStore>());
        var app = builder.Build();
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var invalid = error is BadHttpRequestException;
            context.Response.StatusCode = invalid ? 400 : 500;
            await context.Response.WriteAsJsonAsync(new FleetError(
                invalid ? FleetErrorCode.InvalidRequest : FleetErrorCode.ServerFailure,
                invalid ? "Invalid request." : "Fleet server failed to process the request.")).ConfigureAwait(false);
        }));
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                !context.RequestServices.GetRequiredService<IFleetAgentAuthenticator>().Authenticate(context))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new FleetError(FleetErrorCode.EnrollmentFailure,
                    "A local development token is required.")).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.UseStatusCodePages(async context =>
        {
            var code = context.HttpContext.Response.StatusCode;
            await context.HttpContext.Response.WriteAsJsonAsync(new FleetError(
                code == 404 ? FleetErrorCode.NotFound : FleetErrorCode.InvalidRequest,
                code == 404 ? "Resource not found." : "Invalid request.")).ConfigureAwait(false);
        });
        app.MapGet("/health", () => new FleetVersion(FleetProtocolVersion.Current, true));
        app.MapPost("/agents", (AgentRegistrationRequest request, IDeviceRepository devices) =>
        {
            if (!request.Protocol.IsSupported)
                return Error(400, FleetErrorCode.UnsupportedProtocol, "Supported Fleet protocol is 1.0.");
            if (!RequestValidation.Identity(request.Identity) || !RequestValidation.Version(request.IglooVersion) ||
                request.Capabilities is null || !request.Capabilities.ReadOnlyAssessment)
                return Error(400, FleetErrorCode.InvalidRequest, "Valid identity, versions and assessment capability are required.");
            if (request.AgentVersion != "0.1.0")
                return Error(400, FleetErrorCode.UnsupportedAgentVersion, "Supported development Agent version is 0.1.0.");
            if (!devices.Register(request, DateTimeOffset.UtcNow))
                return Error(409, FleetErrorCode.Conflict, "Identity conflict or development storage capacity reached.");
            return Results.Ok(new AgentRegistrationResponse(FleetProtocolVersion.Current, request.Identity));
        });
        app.MapGet("/agents/{deviceId:guid}", (Guid deviceId, IDeviceRepository devices) =>
            devices.Find(deviceId) is { } device ? Results.Ok(device) :
                Error(404, FleetErrorCode.NotFound, "Device not found."));
        app.MapPost("/heartbeats", (AgentHeartbeat request, IDeviceRepository devices) =>
        {
            if (!request.Protocol.IsSupported)
                return Error(400, FleetErrorCode.UnsupportedProtocol, "Supported Fleet protocol is 1.0.");
            if (!RequestValidation.Identity(request.Identity) || request.Capabilities is null ||
                !request.Capabilities.ReadOnlyAssessment)
                return Error(400, FleetErrorCode.InvalidRequest, "Valid identity and assessment capability are required.");
            return devices.Heartbeat(request, DateTimeOffset.UtcNow) ? Results.NoContent() :
                Error(401, FleetErrorCode.EnrollmentFailure, "Register this identity first.");
        });
        app.MapPost("/assessments", (AssessmentResult request, IDeviceRepository devices, IAssessmentRepository evidence) =>
        {
            if (!request.Protocol.IsSupported)
                return Error(400, FleetErrorCode.UnsupportedProtocol, "Supported Fleet protocol is 1.0.");
            if (!RequestValidation.Assessment(request))
                return Error(400, FleetErrorCode.InvalidRequest, "Invalid or inconsistent assessment evidence.");
            var device = devices.Find(request.Identity.DeviceId);
            if (device?.Registration.Identity != request.Identity)
                return Error(401, FleetErrorCode.EnrollmentFailure, "Register this identity first.");
            if (device.Registration.AgentVersion != request.AgentVersion ||
                device.Registration.IglooVersion != request.IglooVersion)
                return Error(400, FleetErrorCode.InvalidRequest, "Evidence versions must match registration.");
            if (!evidence.Add(new(request, DateTimeOffset.UtcNow)))
                return Error(409, FleetErrorCode.Conflict, "Assessment already exists or development storage capacity reached.");
            return Results.Created($"/assessments/{request.AssessmentId}", request);
        });
        app.MapGet("/assessments/{assessmentId:guid}", (Guid assessmentId, IAssessmentRepository evidence) =>
            evidence.Find(assessmentId) is { } stored ? Results.Ok(stored.Assessment) :
                Error(404, FleetErrorCode.NotFound, "Assessment not found."));
        return app;
    }

    private static IResult Error(int status, FleetErrorCode code, string diagnostic) =>
        Results.Json(new FleetError(code, diagnostic), statusCode: status);
}
