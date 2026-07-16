using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Igloo.Iso;

/// <summary>
/// Verifies OpenPGP cleartext-signed messages (RFC 4880 §7) using BouncyCastle.
/// This is the format Fedora uses for its SHA256SUM / CHECKSUM files:
/// <code>
/// -----BEGIN PGP SIGNED MESSAGE-----
/// Hash: SHA256
///
/// SHA256 (Fedora-KDE-Live-x86_64-40-1.14.iso) = &lt;hash&gt;
/// -----BEGIN PGP SIGNATURE-----
/// ...
/// -----END PGP SIGNATURE-----
/// </code>
/// </summary>
internal static class PgpCleartextVerifier
{
    /// <summary>
    /// Returns <c>true</c> when at least one signature in <paramref name="cleartextMessage"/>
    /// is valid for a key present in <paramref name="publicKeyRingBytes"/>.
    /// Never throws; logs warnings on failure.
    /// </summary>
    internal static bool Verify(byte[] publicKeyRingBytes, string cleartextMessage, ILogger logger,
        string? expectedFingerprint = null)
    {
        try
        {
            // ── Load public key ring ─────────────────────────────────────────
            using var keyInput   = new MemoryStream(publicKeyRingBytes);
            var       keyDecoder = PgpUtilities.GetDecoderStream(keyInput);
            var       keyRing    = new PgpPublicKeyRingBundle(keyDecoder);

            // ── Split into body and signature block ──────────────────────────
            if (!TrySplit(cleartextMessage, out var body, out var sigBlock))
            {
                logger.LogWarning("Could not parse cleartext PGP message structure");
                return false;
            }

            // ── Parse signature list ─────────────────────────────────────────
            using var sigInput   = new MemoryStream(Encoding.ASCII.GetBytes(sigBlock));
            var       sigDecoder = PgpUtilities.GetDecoderStream(sigInput);
            var       factory    = new PgpObjectFactory(sigDecoder);
            var       sigList    = factory.NextPgpObject() as PgpSignatureList;

            if (sigList is null || sigList.Count == 0)
            {
                logger.LogWarning("No PGP signatures found in signature block");
                return false;
            }

            // ── Canonicalize body: strip trailing WS, CRLF line endings ─────
            var bodyBytes = Encoding.UTF8.GetBytes(Canonicalize(body));

            // ── Try each signature until one validates ───────────────────────
            for (int i = 0; i < sigList.Count; i++)
            {
                var sig = sigList[i];
                var key = keyRing.GetPublicKey(sig.KeyId);

                if (key is null)
                {
                    logger.LogDebug("Key ID {KeyId:X16} not found in key ring", sig.KeyId);
                    continue;
                }

                // Pinned trust anchor: the signing key must be the one we expect.
                if (!PinAccepts(keyRing, key, expectedFingerprint, logger))
                    continue;

                sig.InitVerify(key);
                sig.Update(bodyBytes);

                if (sig.Verify())
                {
                    logger.LogInformation("GPG signature valid (key ID {KeyId:X16})", sig.KeyId);
                    return true;
                }
            }

            logger.LogWarning("GPG verification failed - no valid signature matched");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GPG verification threw an unexpected exception");
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TrySplit(string message, out string body, out string sigBlock)
    {
        body     = string.Empty;
        sigBlock = string.Empty;

        const string MsgHeader = "-----BEGIN PGP SIGNED MESSAGE-----";
        const string SigHeader = "-----BEGIN PGP SIGNATURE-----";
        const string SigFooter = "-----END PGP SIGNATURE-----";

        int msgStart = message.IndexOf(MsgHeader, StringComparison.Ordinal);
        int sigStart = message.IndexOf(SigHeader, StringComparison.Ordinal);
        int sigEnd   = message.IndexOf(SigFooter, StringComparison.Ordinal);

        if (msgStart < 0 || sigStart < 0 || sigEnd < 0) return false;

        // Skip the "Hash: xx" headers after the BEGIN line - look for the first blank line
        int scan = message.IndexOf('\n', msgStart) + 1;
        while (scan < sigStart)
        {
            int eol  = message.IndexOf('\n', scan);
            if (eol < 0) break;
            var line = message[scan..eol].Trim();
            scan = eol + 1;
            if (line.Length == 0) break; // blank line → body starts here
        }

        body     = message[scan..sigStart].TrimEnd('\r', '\n');
        sigBlock = message[sigStart..(sigEnd + SigFooter.Length)];
        return true;
    }

    /// <summary>
    /// RFC 4880 §7.1: strip trailing whitespace from each line, join with CRLF.
    /// The last line does NOT receive a trailing CRLF.
    /// </summary>
    private static string Canonicalize(string body)
    {
        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        return string.Join("\r\n", lines.Select(l => l.TrimEnd(' ', '\t')));
    }

    /// <summary>
    /// Pinned-fingerprint trust anchor. Returns true when the 160-bit fingerprint of
    /// <paramref name="signingKey"/> — or, because distros routinely sign with a
    /// subkey, of the PRIMARY key of the ring that contains it — equals
    /// <paramref name="expected"/> (ignoring spaces/colons/case). Pinning the full
    /// fingerprint defeats 64-bit key-ID forgery and a malicious keyserver handing
    /// back a different key; the 64-bit key ID alone is spoofable.
    /// When no fingerprint is pinned this returns true but logs a warning: the
    /// signature then only proves "signed by whatever key the key URL served".
    /// </summary>
    internal static bool PinAccepts(
        PgpPublicKeyRingBundle bundle, PgpPublicKey signingKey, string? expected, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            logger.LogWarning(
                "No signing-key fingerprint pinned - signature only proves possession of the fetched key");
            return true;
        }

        var want = new string(expected.Where(Uri.IsHexDigit).ToArray());

        static bool Matches(PgpPublicKey k, string want) =>
            string.Equals(Convert.ToHexString(k.GetFingerprint()), want, StringComparison.OrdinalIgnoreCase);

        if (Matches(signingKey, want)) return true;

        // Subkey case: the checksum may be signed by a subkey whose fingerprint
        // differs from the published (primary) one. Accept iff the primary key of
        // the ring holding the signing key carries the pinned fingerprint.
        var primary = bundle.GetPublicKeyRing(signingKey.KeyId)?
            .GetPublicKeys().OfType<PgpPublicKey>().FirstOrDefault(k => k.IsMasterKey);
        if (primary is not null && Matches(primary, want)) return true;

        logger.LogWarning(
            "Signing key fingerprint {Actual} (primary {Primary}) does not match pinned {Want} - rejecting signature",
            Convert.ToHexString(signingKey.GetFingerprint()),
            primary is not null ? Convert.ToHexString(primary.GetFingerprint()) : "n/a",
            want);
        return false;
    }
}
