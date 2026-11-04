using System.Text;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using Xunit;

namespace Igloo.Iso.Tests;

public sealed class PgpCleartextVerifierTests
{
    private const string Hash = "b71b64cbbd6e9d1552b48e78e197c0a9678872b0dbbea3251d38b8bab334f6d7";

    private sealed class ChecksumClientFactory(string checksum, byte[] isoBytes) : IHttpClientFactory
    {
        public int IsoDownloads { get; private set; }

#pragma warning disable CA2000 // The returned HttpClient owns the short-lived test handler.
        public HttpClient CreateClient(string name) => new(new Handler(this, checksum, isoBytes));
#pragma warning restore CA2000

        private sealed class Handler(ChecksumClientFactory owner, string checksum, byte[] isoBytes)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/CHECKSUM",
                        StringComparison.Ordinal) == true)
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(checksum),
                    });
                if (request.RequestUri?.AbsolutePath.EndsWith("/target.iso",
                        StringComparison.Ordinal) == true)
                {
                    owner.IsoDownloads++;
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(isoBytes),
                    });
                }
                throw new InvalidOperationException("Unexpected test URL.");
            }
        }
    }

    private static (byte[] Key, string Fingerprint, string Message) Sign(string body)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var key = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral,
            generator.GenerateKeyPair(), DateTime.UtcNow);
        var signer = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral,
            HashAlgorithmTag.Sha256);
        signer.InitSign(PgpSignature.CanonicalTextDocument, key.PrivateKey);
        signer.Update(Encoding.UTF8.GetBytes(body));
        using var armoredBuffer = new MemoryStream();
        using (var armor = new ArmoredOutputStream(armoredBuffer))
        {
            var signature = signer.Generate().GetEncoded();
            armor.Write(signature, 0, signature.Length);
        }
        var signatureBlock = Encoding.ASCII.GetString(armoredBuffer.ToArray());
        return (key.PublicKey.GetEncoded(),
            Convert.ToHexString(key.PublicKey.GetFingerprint()),
            $"-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n{body}\n{signatureBlock}");
    }

    [Fact]
    public void Unsigned_checksum_line_after_valid_signature_is_rejected()
    {
        var body = $"SHA256 (other.iso) = {Hash}";
        var signed = Sign(body);

        PgpCleartextVerifier.Verify(signed.Key, signed.Message,
            NullLogger.Instance, out var verifiedBody, signed.Fingerprint).Should().BeTrue();
        verifiedBody.Should().Be(body);
        PgpCleartextVerifier.Verify(signed.Key,
            signed.Message + $"\nSHA256 (target.iso) = {Hash}\n",
            NullLogger.Instance, signed.Fingerprint).Should().BeFalse();
        PgpCleartextVerifier.Verify(signed.Key,
            $"SHA256 (target.iso) = {Hash}\n" + signed.Message,
            NullLogger.Instance, signed.Fingerprint).Should().BeFalse();
    }

    [Fact]
    public async Task Acquisition_without_manifest_hash_cannot_use_an_unsigned_checksum_line()
    {
        var iso = Encoding.UTF8.GetBytes("unsigned installer bytes");
        var isoHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(iso));
        var signed = Sign($"SHA256 (other.iso) = {Hash}");
        var checksum = signed.Message + $"\nSHA256 (target.iso) = {isoHash}\n";
        var client = new ChecksumClientFactory(checksum, iso);
        var service = new IsoAcquisitionService(client, NullLogger<IsoAcquisitionService>.Instance);
        var spec = new IsoSpecification("test-cleartext-envelope",
            new Uri("https://example.org/target.iso"), "",
            new Uri("https://example.org/CHECKSUM"), null,
            GpgKeyData: signed.Key, GpgKeyFingerprint: signed.Fingerprint);

        var act = () => service.AcquireAsync(spec, null);
        var failure = await act.Should().ThrowAsync<IsoAcquisitionFailureException>();
        failure.Which.Kind.Should().Be(IsoAcquisitionFailureKind.VerificationFailed);
        client.IsoDownloads.Should().Be(0);
    }
}
