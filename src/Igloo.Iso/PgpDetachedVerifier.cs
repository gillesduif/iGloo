using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Igloo.Iso;

/// <summary>
/// Verifies an OpenPGP <b>detached</b> signature against a data file, using
/// BouncyCastle. This is the format Debian and Ubuntu use for their checksum
/// files: a plain <c>SHA256SUMS</c> data file plus a separate signature file
/// (<c>SHA256SUMS.sign</c> / <c>SHA256SUMS.gpg</c>) that signs its raw bytes,
/// in contrast to Fedora's single clear-signed CHECKSUM (see
/// <see cref="PgpCleartextVerifier"/>).
/// </summary>
internal static class PgpDetachedVerifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="detachedSignature"/> is a valid
    /// signature over <paramref name="signedData"/> for a key in
    /// <paramref name="publicKeyRingBytes"/>. Never throws; logs on failure.
    /// </summary>
    internal static bool Verify(
        byte[] publicKeyRingBytes, byte[] signedData, byte[] detachedSignature, ILogger logger,
        string? expectedFingerprint = null)
    {
        try
        {
            using var keyInput = new MemoryStream(publicKeyRingBytes);
            var keyRing = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(keyInput));

            // GetDecoderStream transparently handles both ASCII-armored (.asc /
            // .gpg text) and binary (.sign) detached signatures.
            using var sigInput = new MemoryStream(detachedSignature);
            var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(sigInput));

            var obj = factory.NextPgpObject();
            var sigList = obj as PgpSignatureList;
            if (sigList is null && obj is PgpCompressedData compressed)
                sigList = new PgpObjectFactory(compressed.GetDataStream()).NextPgpObject() as PgpSignatureList;

            if (sigList is null || sigList.Count == 0)
            {
                logger.LogWarning("No PGP signatures found in detached signature");
                return false;
            }

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
                if (!PgpCleartextVerifier.PinAccepts(keyRing, key, expectedFingerprint, logger))
                    continue;

                sig.InitVerify(key);
                sig.Update(signedData);   // detached sigs cover the raw file bytes
                if (sig.Verify())
                {
                    logger.LogInformation("Detached GPG signature valid (key ID {KeyId:X16})", sig.KeyId);
                    return true;
                }
            }

            logger.LogWarning("Detached GPG verification failed - no valid signature matched");
            return false;
        }
        // Fail closed on any malformed key ring / signature: BouncyCastle surfaces corrupt
        // OpenPGP input through these types. An unverifiable checksum is never trusted.
        catch (Exception ex) when (ex is PgpException or IOException or FormatException
            or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            logger.LogWarning(ex, "Detached GPG verification threw an unexpected exception");
            return false;
        }
    }
}
