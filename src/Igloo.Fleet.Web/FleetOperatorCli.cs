using System.Net.Http.Headers;
using Igloo.Fleet.Web.Services;

namespace Igloo.Fleet.Web;

/// <summary>
/// Preserves the original engineering operator CLI while Fleet.Web gains a browser UI.
/// </summary>
internal static class FleetOperatorCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        Environment.ExitCode = 0;

        if (args.Length is < 2 or > 3 || args[0] is not ("GET" or "POST") ||
            !args[1].StartsWith("/v2/operator/", StringComparison.Ordinal) ||
            args[1].Contains("..", StringComparison.Ordinal) || args[1].Contains('\\', StringComparison.Ordinal))
            throw new ArgumentException("Usage: Fleet.Web GET|POST /v2/operator/<resource> [body.json]");
        var origin = new Uri(Environment.GetEnvironmentVariable(FleetServerOptions.ServerUriVariable) ?? "https://localhost:5188/");
        if (origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("Operator access requires an HTTPS origin.");
        using var root = FleetServerTrust.LoadRoot(Environment.GetEnvironmentVariable(FleetServerOptions.CaCertificateVariable)
            ?? throw new InvalidOperationException("Provision IGLOO_FLEET_CA_CERT first."));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => FleetServerTrust.Validate(root, certificate, errors);
        using var client = new HttpClient(handler) { BaseAddress = origin };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Environment.GetEnvironmentVariable(FleetServerOptions.OperatorSecretVariable) ??
            throw new InvalidOperationException("Set the separate operator credential."));
        using var request = new HttpRequestMessage(new HttpMethod(args[0]), new Uri(args[1], UriKind.Relative));
        if (args.Length == 3)
            request.Content = new StringContent(await File.ReadAllTextAsync(args[2]).ConfigureAwait(false),
                System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var output = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            Console.WriteLine(output); // Enrollment plaintext is emitted only by token creation.
        else
        {
            await Console.Error.WriteLineAsync(output).ConfigureAwait(false);
            Environment.ExitCode = 1;
        }

        return Environment.ExitCode;
    }
}
