using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Server;

public sealed class FleetCertificateAuthority(X509Certificate2 root) : IDisposable
{
    public X509Certificate2 Root { get; } = root;

    public IssuedAgentCertificate Issue(string pem, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (pem is not { Length: > 0 and < 16384 })
            throw new PlanningException(FleetErrorCode.CertificateInvalid);
        try
        {
            // Default options verify CSR proof-of-possession. Never import requester extensions or subject.
            var csr = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            using var key = csr.PublicKey.GetRSAPublicKey();
            if (key is null || key.KeySize < 2048)
                throw new PlanningException(FleetErrorCode.CertificateInvalid);
            var request = new CertificateRequest("CN=" + identity.AgentId, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
            using var cert = request.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));
            return new(cert.ExportCertificatePem(), Hash(cert), new DateTimeOffset(cert.NotAfter.ToUniversalTime()));
        }
        catch (CryptographicException)
        {
            throw new PlanningException(FleetErrorCode.CertificateInvalid);
        }
    }

    public bool Validate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Fleet authorization checks durable revocation on every request.
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate) && !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority);
    }

    public static string Hash(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    public static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=iGloo Fleet private CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
    }

    public X509Certificate2 CreateServerCertificate(string hostName = "localhost")
    {
        if (Uri.CheckHostName(hostName) == UriHostNameType.Unknown)
            throw new ArgumentException("Supply an explicit TLS DNS name or IP address.", nameof(hostName));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostName, out var address)) san.AddIpAddress(address);
        else san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(90), RandomNumberGenerator.GetBytes(16));
        using var withKey = certificate.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pfx);
        try
        {
            // Schannel requires a Windows key container for TLS credentials. Without PersistKeySet,
            // the imported user key is removed when the certificate is disposed.
            return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    [SupportedOSPlatform("windows")]
    public static X509Certificate2 LoadProtected(string path)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [SupportedOSPlatform("windows")]
    public static void SaveProtected(string path, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var bytes = certificate.Export(X509ContentType.Pfx);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            file.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => Root.Dispose();
}
