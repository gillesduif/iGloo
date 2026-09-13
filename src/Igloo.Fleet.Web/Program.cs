using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length is < 2 or > 3 || args[0] is not ("GET" or "POST") ||
    !args[1].StartsWith("/v2/operator/", StringComparison.Ordinal) ||
    args[1].Contains("..", StringComparison.Ordinal) || args[1].Contains('\\', StringComparison.Ordinal))
    throw new ArgumentException("Usage: Fleet.Web GET|POST /v2/operator/<resource> [body.json]");
var origin = new Uri(Environment.GetEnvironmentVariable("IGLOO_FLEET_SERVER_URI") ?? "https://localhost:5188/");
if (origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0)
    throw new InvalidOperationException("Operator access requires an HTTPS origin.");
using var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("IGLOO_FLEET_CA_CERT")
    ?? throw new InvalidOperationException("Provision IGLOO_FLEET_CA_CERT first.")).ConfigureAwait(false));
using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true };
handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
{
    if (certificate is null || (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch |
        System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    return chain.Build(certificate);
};
using var client = new HttpClient(handler) { BaseAddress = origin };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
    Environment.GetEnvironmentVariable("IGLOO_FLEET_OPERATOR_SECRET") ??
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
