namespace Igloo.Migration.Chromium;

/// <summary>
/// One decrypted Chromium password entry. <c>Origin</c> is the Chromium
/// logins table's origin_url value; the name avoids the analyzer's Uri
/// naming rule and some entries (android://, chrome://) are not valid URIs.
/// </summary>
public sealed record ChromiumLogin(string Origin, string Username, string Password);

/// <summary>A raw row from the Chromium <c>logins</c> table, still encrypted.</summary>
public sealed record RawLogin(string Origin, string Username, ReadOnlyMemory<byte> EncryptedPassword);

/// <summary>
/// One decrypted Chromium cookie. <c>Value</c> stays bytes on purpose: newer
/// Chromium prefixes the plaintext with a hash of the cookie's domain, and
/// carrying the plaintext verbatim means neither side has to know that. The
/// row it belongs to travels with the copied database, so the domain the hash
/// covers is unchanged.
/// </summary>
public sealed record ChromiumCookie(
    string HostKey, string Name, string Path, ReadOnlyMemory<byte> Value);
