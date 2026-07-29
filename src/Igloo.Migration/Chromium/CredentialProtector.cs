using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Igloo.Migration.Chromium;

/// <summary>
/// Protects decrypted browser credentials for transit on the unencrypted
/// FAT32 staging volume, and reverses the process for tests and for the
/// documented cross-language contract the Linux agents implement.
///
/// Envelope format v1 (see docs/decisions/011-chromium-credential-migration.md):
///   magic  8 bytes  "IGCRD001"
///   salt   16 bytes (PBKDF2-HMAC-SHA256, 600 000 iterations, 32-byte key)
///   nonce  12 bytes (AES-256-GCM)
///   body   ciphertext || 16-byte tag
///
/// The key is derived from the user's Linux password: the same plaintext the
/// manifest already carries during the install window and the agents already
/// consume before redaction, so the envelope introduces no new secret and is
/// never the weakest item on the staging volume.
/// </summary>
public static partial class CredentialProtector
{
    private static readonly byte[] Magic = "IGCRD001"u8.ToArray();

    public const int SaltLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int Iterations = 600_000;

    public static int EnvelopeOverhead => Magic.Length + SaltLength + NonceLength + TagLength;

    public static byte[] Protect(byte[] plaintext, string password)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = DeriveKey(password, salt);

        var body = new byte[plaintext.Length + TagLength];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Encrypt(nonce, plaintext,
                body.AsSpan(0, plaintext.Length), body.AsSpan(plaintext.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var envelope = new byte[EnvelopeOverhead + plaintext.Length];
        Magic.CopyTo(envelope, 0);
        salt.CopyTo(envelope, Magic.Length);
        nonce.CopyTo(envelope, Magic.Length + SaltLength);
        body.CopyTo(envelope, Magic.Length + SaltLength + NonceLength);
        return envelope;
    }

    /// <summary>
    /// Reverses <see cref="Protect"/>. Throws <see cref="CryptographicException"/>
    /// when the password is wrong or the envelope was modified, and
    /// <see cref="InvalidDataException"/> when the format is not recognised.
    /// </summary>
    public static byte[] Unprotect(byte[] envelope, string password)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (envelope.Length < EnvelopeOverhead
            || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an iGloo credential envelope (bad magic or truncated).");
        }

        var salt = envelope.AsSpan(Magic.Length, SaltLength);
        var nonce = envelope.AsSpan(Magic.Length + SaltLength, NonceLength);
        var body = envelope.AsSpan(Magic.Length + SaltLength + NonceLength);

        var key = DeriveKey(password, salt);
        var plaintext = new byte[body.Length - TagLength];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(nonce, body[..^TagLength], body[^TagLength..], plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plaintext;
    }

    /// <summary>Serialises the payload exactly as the Linux agents expect it.</summary>
    public static byte[] BuildPayload(string browserName, IReadOnlyList<ChromiumLogin> logins)
    {
        ArgumentException.ThrowIfNullOrEmpty(browserName);
        ArgumentNullException.ThrowIfNull(logins);

        // Hand-built JSON is avoided on purpose: System.Text.Json escapes
        // correctly for every password the wizard can encounter.
        var payload = new CredentialPayload(
            browserName,
            logins.Select(l => new CredentialPayloadEntry(l.Origin, l.Username, l.Password)).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonContext.Default.CredentialPayload);
    }

    internal static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, 32);

    private sealed record CredentialPayload(
        [property: JsonPropertyName("browser")] string Browser,
        [property: JsonPropertyName("logins")] CredentialPayloadEntry[] Logins);

    private sealed record CredentialPayloadEntry(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("password")] string Password);

    // Source-generated serializer: keeps the library trim-safe and avoids
    // reflection over credential-shaped types at runtime.
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(CredentialPayload))]
    private sealed partial class PayloadJsonContext : JsonSerializerContext;
}
