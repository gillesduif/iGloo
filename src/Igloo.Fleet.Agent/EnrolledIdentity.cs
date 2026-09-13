using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

public sealed record EnrolledIdentityMetadata(DeviceIdentity Identity, Guid EnrollmentId, string CertificateHash);

public sealed class EnrolledIdentityStore(string directory)
{
    private string CertificatePath => Path.Join(directory, "agent-key.dpapi");
    private string MetadataPath => Path.Join(directory, "identity.json");
    public bool Exists => File.Exists(CertificatePath) || File.Exists(MetadataPath);

    public void Save(EnrollmentResponse response, RSA key)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(key);
        if (Exists) throw new InvalidOperationException("Identity already exists. Re-enrollment requires explicit operator replacement.");
        Directory.CreateDirectory(directory);
        using var issued = X509Certificate2.CreateFromPem(response.CertificatePem);
        using var withKey = issued.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pfx);
        try
        {
            using var file = new FileStream(CertificatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
            file.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        using var metadata = new FileStream(MetadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(metadata, new EnrolledIdentityMetadata(response.Identity, response.EnrollmentId,
            issued.GetCertHashString(HashAlgorithmName.SHA256)));
        metadata.Flush(true);
    }

    public (EnrolledIdentityMetadata Metadata, X509Certificate2 Certificate) Load()
    {
        var data = JsonSerializer.Deserialize<EnrolledIdentityMetadata>(File.ReadAllText(MetadataPath))
            ?? throw new InvalidDataException("Invalid Fleet identity metadata.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CertificatePath), null, DataProtectionScope.CurrentUser);
        try
        {
            var certificate = new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.UserKeySet);
            if (certificate.GetCertHashString(HashAlgorithmName.SHA256) != data.CertificateHash ||
                !certificate.HasPrivateKey || data.Identity.DeviceId == Guid.Empty || data.Identity.AgentId == Guid.Empty)
            {
                certificate.Dispose();
                throw new InvalidDataException("Fleet identity does not match the protected certificate.");
            }
            return (data, certificate);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public static class FleetTls
{
    public static HttpClientHandler CreateHandler(X509Certificate2 root, X509Certificate2? identity = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true };
        if (identity is not null) handler.ClientCertificates.Add(identity);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Pinned private CA, no external CRL service.
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        };
        return handler;
    }
}
