using System.Net.Http.Headers;
using Igloo.Core.Abstractions;
using Igloo.Fleet.Agent;
using Igloo.Preflight;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (!args.Contains("--development-local", StringComparer.Ordinal))
    throw new InvalidOperationException("Phase 0 requires --development-local; production enrollment is not implemented.");
var token = Environment.GetEnvironmentVariable("IGLOO_FLEET_DEV_TOKEN");
if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
    throw new InvalidOperationException("Set IGLOO_FLEET_DEV_TOKEN to a development secret of at least 32 characters.");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton<IPreflightChecker>(_ => new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance));
builder.Services.AddSingleton<ReadOnlyAssessment>();
builder.Services.AddSingleton<IDeviceIdentityProvider>(_ => new DevelopmentDeviceIdentityProvider(
    Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "Fleet", "development-identity.json")));
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FleetAssessment");
using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true };
using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5187/"), Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    var identity = host.Services.GetRequiredService<IDeviceIdentityProvider>().GetIdentity();
    var workflow = new FleetAssessmentClient(client, host.Services.GetRequiredService<ReadOnlyAssessment>());
    var result = await workflow.RunAsync(identity, timeout.Token).ConfigureAwait(false);
    logger.LogInformation("Assessment {AssessmentId} for {DeviceId}: {Outcome}, {Eligibility}",
        result.AssessmentId, identity.DeviceId, result.Outcome, result.Eligibility);
}
catch (FleetProtocolException ex)
{
    logger.LogError("Fleet request rejected: {Code}", ex.Error.Code);
    Environment.ExitCode = 1;
}
catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
{
    logger.LogError("Fleet communication failed: {Code}", Igloo.Fleet.Contracts.FleetErrorCode.CommunicationFailure);
    Environment.ExitCode = 1;
}
