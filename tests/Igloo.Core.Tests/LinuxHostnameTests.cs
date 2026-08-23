using FluentAssertions;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class LinuxHostnameTests
{
    [Theory]
    // The bare-metal failure: debian-installer rejected "gilles_dhuyvetter-pc".
    [InlineData("gilles_dhuyvetter", "gilles-dhuyvetter-pc")]
    [InlineData("gilles", "gilles-pc")]
    [InlineData("Gilles", "gilles-pc")]
    [InlineData("jan_peeters_2", "jan-peeters-2-pc")]
    [InlineData("a__b", "a-b-pc")]
    [InlineData("_leading", "leading-pc")]
    [InlineData("trailing_", "trailing-pc")]
    public void Produces_an_rfc1123_hostname(string username, string expected)
    {
        LinuxHostname.FromUsername(username).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    public void Falls_back_when_nothing_usable_is_left(string? username)
    {
        LinuxHostname.FromUsername(username).Should().Be("igloo-pc");
    }

    [Fact]
    public void Stays_within_the_63_character_limit()
    {
        var hostname = LinuxHostname.FromUsername(new string('a', 200));

        hostname.Should().HaveLength(63);
        hostname.Should().EndWith("-pc");
    }

    [Fact]
    public void Never_ends_on_a_hyphen_after_truncation()
    {
        // 60 chars then a separator: truncating naively would leave "...-" before "-pc".
        var hostname = LinuxHostname.FromUsername(new string('a', 60) + "_tail");

        hostname.Should().NotContain("--");
        hostname.Should().MatchRegex("^[a-z0-9][a-z0-9-]*[a-z0-9]$");
    }

    [Fact]
    public void Accepts_only_characters_the_installers_allow()
    {
        var hostname = LinuxHostname.FromUsername("Gilles D'huyvetter!@#");

        hostname.Should().MatchRegex("^[a-z0-9-]+$");
        hostname.Should().Be("gilles-d-huyvetter-pc");
    }

    [Theory]
    // The Windows computer name wins: the machine keeps the name its owner gave it.
    [InlineData("DESKTOP-Living", "desktop-living")]
    [InlineData("Gilles-PC", "gilles-pc")]
    [InlineData("NL-LAPTOP-042", "nl-laptop-042")]
    [InlineData("WIN_11_BOX", "win-11-box")]
    public void Prefers_the_windows_computer_name(string computerName, string expected)
    {
        LinuxHostname.FromMachine(computerName, "gilles").Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("___")]
    public void Falls_back_to_the_username_when_the_computer_name_is_unusable(string? computerName)
    {
        LinuxHostname.FromMachine(computerName, "gilles_dhuyvetter")
            .Should().Be("gilles-dhuyvetter-pc");
    }

    [Fact]
    public void Does_not_append_pc_to_a_computer_name()
    {
        LinuxHostname.FromMachine("DESKTOP-Living", "gilles").Should().NotEndWith("-pc-pc");
    }
}
