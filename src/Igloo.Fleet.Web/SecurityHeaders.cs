namespace Igloo.Fleet.Web;

/// <summary>
/// Response headers for every console page. The CSP is restricted to the console's own origin, which
/// is what makes the air-gap claim enforceable: any asset fetched from elsewhere fails closed (ADR-015).
/// style-src allows inline styles because Fluent UI components set style attributes on their elements.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>HttpContext.Items key for the per-request nonce that the import map script carries.</summary>
    public const string NonceItem = "csp-nonce";

    public static string Nonce(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[NonceItem] as string ?? string.Empty;
    }

    public static Task ApplyAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItem] = nonce;
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            var headers = http.Response.Headers;
            // The only inline script is Blazor's import map, admitted by a per-request nonce.
            headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                $"script-src 'self' 'nonce-{Nonce(http)}'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "object-src 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}
