using System.Globalization;
using System.Threading.RateLimiting;
using Igloo.Fleet.Web;
using Igloo.Fleet.Web.Authentication;
using Igloo.Fleet.Web.Components;
using Igloo.Fleet.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.FluentUI.AspNetCore.Components;

// IGLOO_FLEET_WEB_UI_BOOTSTRAP
if (args.Length > 0 && args[0] is "GET" or "POST")
{
    Environment.ExitCode = await FleetOperatorCli.RunAsync(args).ConfigureAwait(false);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization(options =>
{
    // Every endpoint without explicit metadata requires a signed-in console account.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-iGloo.Fleet.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(FleetAuthenticationEndpoints.SignInRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.Configure<ConsoleOptions>(builder.Configuration.GetSection(ConsoleOptions.SectionName));
builder.Services.Configure<FleetServerOptions>(builder.Configuration.GetSection(FleetServerOptions.SectionName));
builder.Services.AddSingleton(new FleetOperatorStore(builder.Configuration));
builder.Services.AddSingleton(new ConsoleAuditLog(builder.Configuration));
builder.Services.AddSingleton(new ConsolePreferencesStore(builder.Configuration));
builder.Services.AddScoped<ConsoleSession>();
builder.Services.AddScoped<Igloo.Fleet.Web.Presentation.ConsoleShell>();

var requestedSampleFleetData = builder.Configuration.GetValue<bool?>("Fleet:UseSampleData")
    ?? builder.Environment.IsDevelopment();

if (requestedSampleFleetData && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Fleet sample data can only be enabled in the Development environment.");
}

if (requestedSampleFleetData)
{
    builder.Services.AddSingleton<SampleFleetData>();
    builder.Services.AddSingleton<IModuleDataSource, ModuleSampleStore>();
    builder.Services.AddScoped<SampleFleetOperatorClient>();
    builder.Services.AddScoped<IFleetOperatorClient>(services =>
        new AuditingFleetOperatorClient(
            services.GetRequiredService<SampleFleetOperatorClient>(),
            services.GetRequiredService<ConsoleAuditLog>(),
            services.GetRequiredService<ConsoleSession>()));
}
else
{
    builder.Services.AddSingleton<IModuleDataSource, UnavailableModuleDataSource>();
    builder.Services.AddHttpClient<LiveFleetOperatorClient>()
        .ConfigureHttpClient((services, client) =>
            LiveFleetOperatorClient.ConfigureClient(client, services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FleetServerOptions>>().Value))
        .ConfigurePrimaryHttpMessageHandler(services =>
            FleetServerTrust.CreateHandler(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FleetServerOptions>>().Value));
    builder.Services.AddScoped<IFleetOperatorClient>(services =>
        new AuditingFleetOperatorClient(
            services.GetRequiredService<LiveFleetOperatorClient>(),
            services.GetRequiredService<ConsoleAuditLog>(),
            services.GetRequiredService<ConsoleSession>()));
}

var app = builder.Build();

// Fleet.Web stays an operator UI boundary; no direct Domain/Persistence access.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(SecurityHeaders.ApplyAsync);

// Only fully translated cultures are resolvable, and only by explicit choice (cookie), never from
// Accept-Language: a browser set to Dutch must not get a Dutch navigation over English pages.
var offeredCultures = ConsoleOptions.OfferedCultures.Select(CultureInfo.GetCultureInfo).ToArray();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(offeredCultures[0]),
    SupportedCultures = offeredCultures,
    SupportedUICultures = offeredCultures,
    RequestCultureProviders = [new CookieRequestCultureProvider()]
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();

app.MapFleetAuthenticationEndpoints();
app.MapExportEndpoints();
app.MapPlanningExportEndpoints();
app.MapModuleExportEndpoints();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);
