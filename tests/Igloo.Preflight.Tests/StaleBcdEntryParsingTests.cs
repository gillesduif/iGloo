using FluentAssertions;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

/// <summary>
/// Pins the <c>bcdedit /enum firmware</c> parsing. The listing arrives with CRLF, and a
/// parser that assumes "\n\n" between entries silently finds one identifier instead of
/// all of them - which left three leftover entries on a real machine while the log said
/// one had been removed.
/// </summary>
public sealed class StaleBcdEntryParsingTests
{
    private const string Description = "iGloo distribution installer";

    // Trimmed from real output, CRLF preserved.
    private const string Listing =
        "Firmware Boot Manager\r\n" +
        "---------------------\r\n" +
        "identifier              {fwbootmgr}\r\n" +
        "displayorder            {bootmgr}\r\n" +
        "timeout                 1\r\n" +
        "\r\n" +
        "Windows Boot Manager\r\n" +
        "--------------------\r\n" +
        "identifier              {4f9e6242-6f1a-11f0-b519-f7b20eb4bccb}\r\n" +
        "path                    \\EFI\\BOOT\\BOOTX64.EFI\r\n" +
        "description             iGloo distribution installer\r\n" +
        "\r\n" +
        "Windows Boot Manager\r\n" +
        "--------------------\r\n" +
        "identifier              {4f9e6244-6f1a-11f0-b519-f7b20eb4bccb}\r\n" +
        "path                    \\EFI\\BOOT\\BOOTX64.EFI\r\n" +
        "description             iGloo distribution installer\r\n" +
        "\r\n" +
        "Windows Boot Manager\r\n" +
        "--------------------\r\n" +
        "identifier              {4f9e6246-6f1a-11f0-b519-f7b20eb4bccb}\r\n" +
        "path                    \\EFI\\BOOT\\BOOTX64.EFI\r\n" +
        "description             iGloo distribution installer\r\n" +
        "\r\n" +
        "Windows Boot Manager\r\n" +
        "--------------------\r\n" +
        "identifier              {bootmgr}\r\n" +
        "path                    \\EFI\\Microsoft\\Boot\\bootmgfw.efi\r\n" +
        "description             Windows Boot Manager\r\n";

    [Fact]
    public void Finds_every_leftover_entry_not_just_the_first()
    {
        DirectInstallService.ParseStaleBcdIds(Listing, Description).Should().Equal(
            "{4f9e6242-6f1a-11f0-b519-f7b20eb4bccb}",
            "{4f9e6244-6f1a-11f0-b519-f7b20eb4bccb}",
            "{4f9e6246-6f1a-11f0-b519-f7b20eb4bccb}");
    }

    [Fact]
    public void Never_returns_the_windows_boot_manager()
    {
        DirectInstallService.ParseStaleBcdIds(Listing, Description)
            .Should().NotContain(id => id == "{bootmgr}");
    }

    [Fact]
    public void Handles_lf_only_output_too()
    {
        DirectInstallService.ParseStaleBcdIds(
                Listing.Replace("\r\n", "\n", StringComparison.Ordinal), Description)
            .Should().HaveCount(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_nothing_when_bcdedit_gave_nothing(string? listing)
    {
        DirectInstallService.ParseStaleBcdIds(listing, Description).Should().BeEmpty();
    }

    [Fact]
    public void Returns_nothing_when_no_entry_carries_the_description()
    {
        DirectInstallService.ParseStaleBcdIds(Listing, "Some other loader")
            .Should().BeEmpty();
    }
}
