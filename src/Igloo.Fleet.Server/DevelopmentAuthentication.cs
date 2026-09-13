using System.Security.Cryptography;
using System.Text;

namespace Igloo.Fleet.Server;

/// <summary>Transport authentication seam for a future certificate-backed implementation.</summary>
public interface IFleetAgentAuthenticator
{
    bool Authenticate(HttpContext context);
}

/// <summary>Shared development secret, not device authentication or production enrollment.</summary>
public sealed class DevelopmentTokenAuthenticator : IFleetAgentAuthenticator
{
    private readonly byte[] _expectedHash;

    public DevelopmentTokenAuthenticator(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length < 32)
            throw new ArgumentException("Development token must contain at least 32 characters.", nameof(token));
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
    }

    public bool Authenticate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        return CryptographicOperations.FixedTimeEquals(_expectedHash, supplied);
    }
}
