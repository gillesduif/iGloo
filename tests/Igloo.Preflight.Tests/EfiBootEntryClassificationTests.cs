using System.Text;
using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

public class EfiBootEntryClassificationTests
{
    [Theory]
    [InlineData("ubuntu", true)]
    [InlineData("Fedora", true)]
    [InlineData("Linux Boot Manager", true)]
    [InlineData("GRUB", true)]
    [InlineData("Pop!_OS 22.04", true)]
    [InlineData("Windows Boot Manager", false)]
    [InlineData("iGloo distribution installer", false)]
    [InlineData("UEFI: Samsung SSD", false)]
    [InlineData("", false)]
    public void Linux_classification_never_matches_windows_or_igloo(string description, bool expected)
    {
        EfiBootEntries.IsLinuxDescription(description).Should().Be(expected);
    }

    [Theory]
    [InlineData("iGloo distribution installer", true)]
    [InlineData("IGLOO installer", true)]
    [InlineData("Windows Boot Manager", false)]
    [InlineData("ubuntu", false)]
    public void Igloo_entries_are_recognized_case_insensitively(string description, bool expected)
    {
        EfiBootEntries.IsIglooDescription(description).Should().Be(expected);
    }

    [Fact]
    public void Description_is_parsed_from_a_load_option_up_to_the_nul()
    {
        var option = DirectInstallService.BuildEfiLoadOption(
            1, 2048, 4096, Guid.NewGuid(), @"\EFI\test.efi", "My Loader");

        EfiBootEntries.ParseDescription(option).Should().Be("My Loader");
    }

    [Fact]
    public void Truncated_load_option_yields_an_empty_description()
    {
        EfiBootEntries.ParseDescription([1, 0, 0, 0, 0]).Should().BeEmpty();
        EfiBootEntries.ParseDescription(Encoding.Unicode.GetBytes("x")).Should().BeEmpty();
    }
}
