using FluentAssertions;
using Igloo.App.ViewModels;
using Xunit;

namespace Igloo.App.Tests;

public class LinuxUsernameRulesTests
{
    [Theory]
    [InlineData("gilles", true)]
    [InlineData("user42", true)]
    [InlineData("a-b_c", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("root", false)]
    [InlineData("ROOT", false)]
    [InlineData("www-data", false)]
    [InlineData("42user", false)]
    [InlineData("_user", false)]
    [InlineData("Gilles", false)]
    [InlineData("gil les", false)]
    [InlineData("gillès", false)]
    public void Validates_against_useradd_rules(string name, bool expected)
    {
        LinuxUsernameRules.IsValid(name).Should().Be(expected);
    }

    [Fact]
    public void Names_longer_than_32_chars_are_rejected()
    {
        LinuxUsernameRules.IsValid(new string('a', 32)).Should().BeTrue();
        LinuxUsernameRules.IsValid(new string('a', 33)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Gilles", "gilles")]
    [InlineData("Gilles D'huyvetter", "gilles_d_huyvetter")]
    [InlineData("42cats", "cats")]
    [InlineData("___", "user")]
    [InlineData("", "user")]
    public void Sanitize_produces_a_valid_username_from_a_windows_account_name(
        string windows, string expected)
    {
        var sanitized = LinuxUsernameRules.Sanitize(windows);

        sanitized.Should().Be(expected);
        LinuxUsernameRules.IsValid(sanitized).Should().BeTrue();
    }

    [Fact]
    public void Sanitize_truncates_to_32_chars()
    {
        LinuxUsernameRules.Sanitize(new string('a', 50)).Should().HaveLength(32);
    }
}
