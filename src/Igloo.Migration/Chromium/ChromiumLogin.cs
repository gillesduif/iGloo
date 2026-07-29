namespace Igloo.Migration.Chromium;

/// <summary>
/// One decrypted Chromium password entry. <c>Origin</c> is the Chromium
/// logins table's origin_url value; the name avoids the analyzer's Uri
/// naming rule and some entries (android://, chrome://) are not valid URIs.
/// </summary>
public sealed record ChromiumLogin(string Origin, string Username, string Password);

/// <summary>A raw row from the Chromium <c>logins</c> table, still encrypted.</summary>
public sealed record RawLogin(string Origin, string Username, ReadOnlyMemory<byte> EncryptedPassword);
