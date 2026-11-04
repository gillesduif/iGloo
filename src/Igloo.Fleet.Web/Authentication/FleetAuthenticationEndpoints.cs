using System.Security.Claims;
using Igloo.Fleet.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Igloo.Fleet.Web.Authentication;

internal static class FleetAuthenticationEndpoints
{
    public const string SignInRateLimitPolicy = "sign-in";

    internal static IEndpointRouteBuilder MapFleetAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/setup", SetupAsync).AllowAnonymous().RequireRateLimiting(SignInRateLimitPolicy);
        endpoints.MapPost("/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting(SignInRateLimitPolicy);
        endpoints.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        endpoints.MapPost("/auth/culture", CultureAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context,
        FleetOperatorStore store,
        ConsoleAuditLog audit,
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

            audit.Append(new ConsoleAuditEntry(DateTimeOffset.UtcNow, account.Username, "Setup", account.OperatorId.ToString("D"), "Succeeded", null));
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
        ConsoleAuditLog audit,
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

        var username = form["username"].ToString();
        var account = store.ValidateCredentials(
            username,
            form["password"].ToString());

        if (account is null)
        {
            // The attempted user name is recorded; the password never is.
            audit.Append(new ConsoleAuditEntry(DateTimeOffset.UtcNow, username.Trim(), "SignInFailed", null, "Failed", null));
            return Results.Redirect("/login?error=invalid");
        }

        var rememberMe = string.Equals(
            form["rememberMe"].ToString(),
            "on",
            StringComparison.OrdinalIgnoreCase);

        await SignInAsync(context, account, rememberMe).ConfigureAwait(false);
        audit.Append(new ConsoleAuditEntry(DateTimeOffset.UtcNow, account.Username, "SignIn", account.OperatorId.ToString("D"), "Succeeded", rememberMe ? "persistent" : null));

        return Results.Redirect(
            NormalizeReturnUrl(form["returnUrl"].ToString()));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        ConsoleAuditLog audit,
        IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        var account = ConsoleSession.FromPrincipal(context.User);
        await context.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);
        audit.Append(new ConsoleAuditEntry(DateTimeOffset.UtcNow, account.Username, "SignOut", account.AccountId, "Succeeded", null));

        return Results.Redirect("/login");
    }

    private static async Task<IResult> CultureAsync(
        HttpContext context,
        ConsolePreferencesStore preferences,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var culture = form["culture"].ToString();
        if (!ConsoleOptions.OfferedCultures.Contains(culture, StringComparer.Ordinal))
        {
            return Results.BadRequest();
        }

        var account = ConsoleSession.FromPrincipal(context.User);
        preferences.Set(account.AccountId, preferences.Get(account.AccountId) with { Culture = culture });
        context.Response.Cookies.Append(
            Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName,
            Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.MakeCookieValue(new Microsoft.AspNetCore.Localization.RequestCulture(culture)),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.UtcNow.AddYears(1) });
        return Results.Redirect(NormalizeReturnUrl(form["returnUrl"].ToString()));
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
            new Claim(ConsoleSession.UsernameClaim, account.Username)
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
