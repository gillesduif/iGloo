using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Igloo.Migration.Chromium;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class CredentialProtectorTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        var plaintext = Encoding.UTF8.GetBytes("{\"browser\":\"Brave\",\"logins\":[]}");
        var envelope = CredentialProtector.Protect(plaintext, Password);

        CredentialProtector.Unprotect(envelope, Password).Should().Equal(plaintext);
    }

    [Fact]
    public void Protect_ProducesEnvelopeWithMagicSaltAndNonce()
    {
        var plaintext = Encoding.UTF8.GetBytes("x");
        var envelope = CredentialProtector.Protect(plaintext, Password);

        // magic(8) || salt(16) || nonce(12) || ct+tag
        envelope.Length.Should().Be(CredentialProtector.EnvelopeOverhead + plaintext.Length);
        Encoding.ASCII.GetString(envelope, 0, 8).Should().Be("IGCRD001");
    }

    [Fact]
    public void Protect_UsesRandomSaltAndNonce()
    {
        var plaintext = Encoding.UTF8.GetBytes("same input");
        CredentialProtector.Protect(plaintext, Password)
            .Should().NotEqual(CredentialProtector.Protect(plaintext, Password));
    }

    [Fact]
    public void Unprotect_WrongPassword_Throws()
    {
        var envelope = CredentialProtector.Protect(Encoding.UTF8.GetBytes("data"), Password);

        var act = () => CredentialProtector.Unprotect(envelope, "wrong password");
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var envelope = CredentialProtector.Protect(Encoding.UTF8.GetBytes("data"), Password);
        envelope[^20] ^= 0xFF; // flip a ciphertext bit, keep the tag

        var act = () => CredentialProtector.Unprotect(envelope, Password);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_BadMagic_ThrowsInvalidData()
    {
        var envelope = CredentialProtector.Protect(Encoding.UTF8.GetBytes("data"), Password);
        envelope[0] = (byte)'X';

        var act = () => CredentialProtector.Unprotect(envelope, Password);
        act.Should().Throw<InvalidDataException>();
    }

    // Cross-language contract: this exact envelope was produced by the Python
    // implementation in tools/chromium-crypto (KAT-validated against the NIST
    // GCM vectors). The Linux agents decrypt the same format; if C# cannot
    // reproduce this plaintext, the two sides have drifted apart.
    [Fact]
    public void Unprotect_PythonProducedEnvelope_MatchesInteropVector()
    {
        const string envelopeHex =
            "4947435244303031000102030405060708090a0b0c0d0e0f101112131415161718191a1b" +
            "77301fb77f88db12b998a3ea8b9c1b9d7d5dbaa7ee15b9c6a61237c8a4d62786f0b0add" +
            "3af9ad459be7b816c84927830316b0371e13f190b7ce6ef46d5ec06b207c542ca4ca956" +
            "db39727f9a3d8dddf39da0654e02d1021e7ec314b48524b411c7ee9c122d47983ed5356" +
            "805202cb645fffff9f5b76355c3adcba7066a4b4ee8a4203fe8b3945ec7ff970b3fa707" +
            "d335b5a614cff46a373817224805aa08e7326ded45b3bce84bc4e873d0a3272fa0348f8" +
            "c2847173fd1dc00b9189b416b4b3748ba83d27eef55cfd5";

        var plaintext = CredentialProtector.Unprotect(
            Convert.FromHexString(envelopeHex), Password);

        Encoding.UTF8.GetString(plaintext).Should().Be(
            "{\"browser\":\"Google Chrome\",\"logins\":[" +
            "{\"url\":\"https://example.com/login\",\"username\":\"alice\",\"password\":\"s3cret!\"}," +
            "{\"url\":\"https://test.invalid\",\"username\":\"bob\",\"password\":\"p@ss w0rd\"}]}");
    }

    [Fact]
    public void BuildPayload_ProducesExpectedJsonShape()
    {
        const string trickyPassword = "p\"a\\ss";
        var payload = CredentialProtector.BuildPayload(
            "Vivaldi",
            [new ChromiumLogin("https://a.example", "user", trickyPassword)]);

        var json = Encoding.UTF8.GetString(payload);
        json.Should().Contain("\"browser\":\"Vivaldi\"");
        json.Should().Contain("\"username\":\"user\"");

        // The wire format must round-trip every password exactly, regardless
        // of which escape style the encoder picks (\" vs ).
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("logins")[0].GetProperty("password").GetString()
            .Should().Be(trickyPassword);
    }
}
