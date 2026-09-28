using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Igloo.Fleet.Web.Authentication;
using ColorlibHQ.AdminLTE.AspNetCore;
using Igloo.Fleet.Web;
using Igloo.Fleet.Web.Components;

// IGLOO_FLEET_WEB_UI_BOOTSTRAP
if (args.Length > 0 && args[0] is "GET" or "POST")
{
    Environment.ExitCode = await FleetOperatorCli.RunAsync(args).ConfigureAwait(false);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAdminLte(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorComponents();
builder.Services.AddAuthorization();
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
builder.Services.AddSingleton(new FleetOperatorStore(builder.Configuration));
builder.Services.AddHttpClient<FleetStatusClient>(client =>
{
    client.BaseAddress = new Uri(
        Environment.GetEnvironmentVariable("IGLOO_FLEET_SERVER_URI")
        ?? "https://localhost:5188/");
});
var requestedSampleFleetData = builder.Configuration.GetValue<bool?>("Fleet:UseSampleData")
    ?? builder.Environment.IsDevelopment();

if (requestedSampleFleetData && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Fleet sample data can only be enabled in the Development environment.");
}

if (requestedSampleFleetData)
{
    builder.Services.AddScoped<IFleetDeviceDataSource>(
        _ => new SampleFleetDeviceDataSource());
}
else
{
    builder.Services.AddScoped<IFleetDeviceDataSource>(
        services => new LiveFleetDeviceDataSource(
            services.GetRequiredService<FleetStatusClient>()));
}
var app = builder.Build();

// Fleet.Web stays an operator UI boundary; no direct Domain/Persistence access.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapFleetAuthenticationEndpoints();

app.MapRazorComponents<App>();

await app.RunAsync().ConfigureAwait(false);
