using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class WifiProfileParsingTests : IDisposable
{
    private readonly string _dir = Path.Join(
        Path.GetTempPath(), "igloo-wifi-tests-" + Guid.NewGuid().ToString("N"));

    public WifiProfileParsingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteProfile(string ssid, string auth, string? key, bool hidden = false)
    {
        var keyXml = key is null ? "" : $"""
            <sharedKey><keyType>passPhrase</keyType><protected>false</protected>
            <keyMaterial>{key}</keyMaterial></sharedKey>
            """;
        var path = Path.Join(_dir, ssid + ".xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{ssid}</name>
              <SSIDConfig>
                <SSID><name>{ssid}</name></SSID>
                <nonBroadcast>{(hidden ? "true" : "false")}</nonBroadcast>
              </SSIDConfig>
              <MSM><security>
                <authEncryption><authentication>{auth}</authentication><encryption>AES</encryption></authEncryption>
                {keyXml}
              </security></MSM>
            </WLANProfile>
            """);
        return path;
    }

    [Fact]
    public void Wpa2_psk_profile_parses_with_key_and_primary_flag()
    {
        var path = WriteProfile("HomeNet", "WPA2PSK", "s3cretpass");

        var net = WindowsWifiScanner.ParseProfile(path, ["HomeNet"]);

        net.Should().NotBeNull();
        net!.Ssid.Should().Be("HomeNet");
        net.Security.Should().Be("wpa-psk");
        net.Psk.Should().Be("s3cretpass");
        net.IsPrimary.Should().BeTrue();
        net.Hidden.Should().BeFalse();
    }

    [Fact]
    public void Open_network_has_no_key()
    {
        var net = WindowsWifiScanner.ParseProfile(
            WriteProfile("CoffeeShop", "open", key: null), []);

        net!.Security.Should().Be("open");
        net.Psk.Should().BeNull();
        net.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public void Enterprise_network_is_recorded_as_unsupported_without_credentials()
    {
        var net = WindowsWifiScanner.ParseProfile(
            WriteProfile("CorpNet", "WPA2", key: null), []);

        net!.Security.Should().Be("unsupported");
        net.Psk.Should().BeNull();
    }

    [Fact]
    public void Hidden_network_flag_is_carried_over()
    {
        var net = WindowsWifiScanner.ParseProfile(
            WriteProfile("Stealth", "WPA3SAE", "passphrase", hidden: true), []);

        net!.Hidden.Should().BeTrue();
        net.Security.Should().Be("wpa-psk", "WPA3 personal (SAE) uses a passphrase too");
    }

    [Fact]
    public void Malformed_xml_yields_null_not_an_exception()
    {
        var path = Path.Join(_dir, "broken.xml");
        File.WriteAllText(path, "<not-a-profile>");

        WindowsWifiScanner.ParseProfile(path, []).Should().BeNull();
    }

    [Theory]
    [InlineData("WPAPSK", "wpa-psk")]
    [InlineData("WPA2PSK", "wpa-psk")]
    [InlineData("WPA3SAE", "wpa-psk")]
    [InlineData("open", "open")]
    [InlineData("NONE", "open")]
    [InlineData("WPA2", "unsupported")]
    [InlineData("WPA3ENT", "unsupported")]
    public void Security_normalization_matches_the_agent_contract(string auth, string expected)
    {
        WindowsWifiScanner.NormaliseSecurity(auth, "key").security.Should().Be(expected);
    }
}
