using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Igloo.Fleet.Web.Services;

internal sealed record ConsoleAccount(string AccountId, string Username, string DisplayName, string Role)
{
    public static readonly ConsoleAccount Anonymous = new("anonymous", "anonymous", "Anonymous", string.Empty);
}

/// <summary>The console account behind the current circuit or request.</summary>
internal sealed class ConsoleSession(AuthenticationStateProvider authenticationState)
{
    public const string UsernameClaim = "igloo:operator_username";

    public async Task<ConsoleAccount> GetAccountAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync().ConfigureAwait(false);
        return FromPrincipal(state.User);
    }

    public static ConsoleAccount FromPrincipal(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return ConsoleAccount.Anonymous;
        }

        var displayName = user.Identity.Name ?? string.Empty;
        return new ConsoleAccount(
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? displayName,
            user.FindFirst(UsernameClaim)?.Value ?? displayName,
            displayName,
            user.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty);
    }
}
