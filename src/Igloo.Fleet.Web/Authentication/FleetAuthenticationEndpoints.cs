using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Igloo.Fleet.Web.Authentication;

internal static class FleetAuthenticationEndpoints
{
    internal static IEndpointRouteBuilder MapFleetAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/setup", SetupAsync).AllowAnonymous();
        endpoints.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        endpoints.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context,
        FleetOperatorStore store,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        var form = await context.Request.ReadFormAsync(cancellationToken)
            .ConfigureAwait(false);

        if (store.HasOperators())
        {
            return Results.Redirect("/login");
        }

        var password = form["password"].ToString();
        if (!string.Equals(
            password,
            form["confirmPassword"].ToString(),
            StringComparison.Ordinal))
        {
            return Results.Redirect("/setup?error=password-mismatch");
        }

        try
        {
            var account = store.CreateInitialAdministrator(
                form["username"].ToString(),
                form["displayName"].ToString(),
                password);

            await SignInAsync(context, account, false).ConfigureAwait(false);
            return Results.Redirect("/");
        }
        catch (ArgumentException)
        {
            return Results.Redirect("/setup?error=validation");
        }
        catch (InvalidOperationException)
        {
            return Results.Redirect("/login");
        }
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        FleetOperatorStore store,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        var form = await context.Request.ReadFormAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!store.HasOperators())
        {
            return Results.Redirect("/setup");
        }

        var account = store.ValidateCredentials(
            form["username"].ToString(),
            form["password"].ToString());

        if (account is null)
        {
            return Results.Redirect("/login?error=invalid");
        }

        var rememberMe = string.Equals(
            form["rememberMe"].ToString(),
            "on",
            StringComparison.OrdinalIgnoreCase);

        await SignInAsync(context, account, rememberMe).ConfigureAwait(false);

        return Results.Redirect(
            NormalizeReturnUrl(form["returnUrl"].ToString()));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        await context.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        return Results.Redirect("/login");
    }

    private static async Task SignInAsync(
        HttpContext context,
        FleetOperatorAccount account,
        bool isPersistent)
    {
        var claims = new[]
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                account.OperatorId.ToString("D")),
            new Claim(ClaimTypes.Name, account.DisplayName),
            new Claim(ClaimTypes.Role, account.Role),
            new Claim("igloo:operator_username", account.Username)
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme));

        var properties = new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            AllowRefresh = true,
            ExpiresUtc = isPersistent
                ? DateTimeOffset.UtcNow.AddDays(14)
                : null
        };

        await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                properties)
            .ConfigureAwait(false);
    }

    private static string NormalizeReturnUrl(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate[0] != '/' ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.Contains("://", StringComparison.Ordinal))
        {
            return "/";
        }

        return candidate;
    }
}