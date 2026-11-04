using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Igloo.Fleet.Web.Services;

/// <summary>
/// Validates the Fleet Server certificate against the independently provisioned private root.
/// Shared by the operator CLI and the browser host so both apply the same policy.
/// </summary>
internal static class FleetServerTrust
{
    private const string ServerAuthenticationEku = "1.3.6.1.5.5.7.3.1";

    public static X509Certificate2 LoadRoot(string pemPath) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(pemPath));

    public static bool Validate(X509Certificate2 root, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationEku));
        return chain.Build(certificate);
    }

    /// <summary>A handler that fails closed when the root is missing or unreadable.</summary>
    public static HttpClientHandler CreateHandler(FleetServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        X509Certificate2? root = null;
        var path = options.ResolveCaCertificatePath();
        if (path is not null && File.Exists(path))
        {
            try
            {
                root = LoadRoot(path);
            }
            catch (CryptographicException)
            {
                root = null;
            }
        }

        return new TrustedRootHandler(root);
    }

    /// <summary>Owns the root certificate for as long as the handler lives.</summary>
    private sealed class TrustedRootHandler : HttpClientHandler
    {
        private readonly X509Certificate2? _root;

        public TrustedRootHandler(X509Certificate2? root)
        {
            _root = root;
            AllowAutoRedirect = false;
            UseProxy = false;
            CheckCertificateRevocationList = true;
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                _root is not null && Validate(_root, certificate, errors);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _root?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
