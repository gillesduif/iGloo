using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using Xunit;

namespace Igloo.Iso.Tests;

/// <summary>
/// Characterization tests for <see cref="PgpDetachedVerifier"/>, the trust anchor of the
/// Debian/Ubuntu download path. A freshly generated RSA key signs test data; the tests
/// pin the accept/reject rules: valid signature accepted, tampered data rejected,
/// fingerprint mismatch rejected, malformed input rejected without throwing.
/// </summary>
public class PgpDetachedVerifierTests : IClassFixture<PgpDetachedVerifierTests.SigningKeyFixture>
{
    public sealed class SigningKeyFixture
    {
        public PgpKeyPair KeyPair { get; }
        public byte[] PublicKeyRingBytes { get; }
        public string Fingerprint { get; }
        public byte[] Data { get; } = Encoding.UTF8.GetBytes("hash-a  file-a.iso\nhash-b  file-b.iso\n");
        public byte[] Signature { get; }

        public SigningKeyFixture()
        {
            var generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            AsymmetricCipherKeyPair rsa = generator.GenerateKeyPair();

            KeyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, rsa, DateTime.UtcNow);
            PublicKeyRingBytes = KeyPair.PublicKey.GetEncoded();
            Fingerprint = Convert.ToHexString(KeyPair.PublicKey.GetFingerprint());
            Signature = Sign(KeyPair, Data);
        }

        public static byte[] Sign(PgpKeyPair keyPair, byte[] data)
        {
            var signer = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
            signer.InitSign(PgpSignature.BinaryDocument, keyPair.PrivateKey);
            signer.Update(data);
            return signer.Generate().GetEncoded();
        }
    }

    private readonly SigningKeyFixture _key;

    public PgpDetachedVerifierTests(SigningKeyFixture key) => _key = key;

    private bool Verify(byte[]? keyRing = null, byte[]? data = null, byte[]? sig = null,
        string? fingerprint = null)
        => PgpDetachedVerifier.Verify(
            keyRing ?? _key.PublicKeyRingBytes,
            data ?? _key.Data,
            sig ?? _key.Signature,
            NullLogger.Instance,
            fingerprint);

    [Fact]
    public void Valid_signature_without_pin_is_accepted()
    {
        Verify().Should().BeTrue();
    }

    [Fact]
    public void Valid_signature_with_matching_pin_is_accepted_ignoring_formatting()
    {
        var spaced = string.Join(" ",
            Enumerable.Range(0, _key.Fingerprint.Length / 4)
                .Select(i => _key.Fingerprint.Substring(i * 4, 4)));

        Verify(fingerprint: _key.Fingerprint.ToLowerInvariant()).Should().BeTrue();
        Verify(fingerprint: spaced).Should().BeTrue();
    }

    [Fact]
    public void Wrong_fingerprint_pin_rejects_a_cryptographically_valid_signature()
    {
        var wrongPin = new string('A', 40);

        Verify(fingerprint: wrongPin).Should().BeFalse(
            "a valid signature from the WRONG key is exactly the attack pinning defends against");
    }

    [Fact]
    public void Tampered_data_is_rejected()
    {
        var tampered = (byte[])_key.Data.Clone();
        tampered[0] ^= 0xFF;

        Verify(data: tampered).Should().BeFalse();
    }

    [Fact]
    public void Signature_from_an_unknown_key_is_rejected()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var otherKey = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), DateTime.UtcNow);

        var foreignSignature = SigningKeyFixture.Sign(otherKey, _key.Data);

        Verify(sig: foreignSignature).Should().BeFalse("the signer is not in the trusted key ring");
    }

    [Fact]
    public void Garbage_inputs_return_false_instead_of_throwing()
    {
        Verify(sig: [1, 2, 3]).Should().BeFalse();
        Verify(keyRing: [9, 9, 9]).Should().BeFalse();
    }
}
