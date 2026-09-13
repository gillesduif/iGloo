using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

public class BootManagerRepairTests
{
    // Verbatim from the reporter's machine, CRLF and all - the line endings are
    // the reason the stale-entry parser once matched a single block.
    private const string FirmwareListing =
        "Firmware Boot Manager\r\n" +
        "---------------------\r\n" +
        "identifier              {fwbootmgr}\r\n" +
        "displayorder            {bootmgr}\r\n" +
        "                        {7619dcc8-fafe-11d9-b411-000476eba25f}\r\n" +
        "timeout                 1\r\n" +
        "\r\n" +
        "Windows Boot Manager\r\n" +
        "--------------------\r\n" +
        "identifier              {bootmgr}\r\n" +
        "device                  partition=\\Device\\HarddiskVolume1\r\n" +
        "path                    \\EFI\\Microsoft\\Boot\\bootmgfw.efi\r\n" +
        "description             Windows Boot Manager\r\n" +
        "\r\n" +
        "Firmware Application (101fffff)\r\n" +
        "-------------------------------\r\n" +
        "identifier              {7619dcc8-fafe-11d9-b411-000476eba25f}\r\n" +
        "device                  partition=\\Device\\HarddiskVolume1\r\n" +
        "path                    \\EFI\\debian\\shimx64.efi\r\n" +
        "description             debian\r\n";

    [Fact]
    public void Reads_the_loader_path_of_the_windows_entry()
    {
        BootManagerRepair.ParseCurrentPath(FirmwareListing)
            .Should().Be(@"\EFI\Microsoft\Boot\bootmgfw.efi");
    }

    // The debian block also has a "path" line and comes later; picking the wrong
    // block would report success while nothing had changed.
    [Fact]
    public void Does_not_confuse_another_entrys_path_for_the_windows_one()
    {
        BootManagerRepair.ParseCurrentPath(FirmwareListing)
            .Should().NotBe(@"\EFI\debian\shimx64.efi");
    }

    [Fact]
    public void Reads_the_repaired_path_back()
    {
        var repaired = FirmwareListing.Replace(
            @"\EFI\Microsoft\Boot\bootmgfw.efi", @"\EFI\debian\shimx64.efi",
            StringComparison.Ordinal);

        BootManagerRepair.ParseCurrentPath(repaired)
            .Should().Be(@"\EFI\debian\shimx64.efi");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Firmware Boot Manager\r\nidentifier {fwbootmgr}\r\n")]
    public void Returns_null_when_there_is_no_windows_entry(string? listing)
    {
        BootManagerRepair.ParseCurrentPath(listing).Should().BeNull();
    }

    [Fact]
    public void Shim_path_follows_the_distributions_efi_directory()
    {
        BootManagerRepair.ShimPathFor("debian").Should().Be(@"\EFI\debian\shimx64.efi");
        BootManagerRepair.ShimPathFor("fedora").Should().Be(@"\EFI\fedora\shimx64.efi");
    }

    // Pointing the firmware at a file that is not there leaves a machine that
    // boots neither system, so presence is checked and never assumed.
    [Fact]
    public void Reports_a_missing_loader_rather_than_assuming_it_is_there()
    {
        var esp = Directory.CreateTempSubdirectory("igloo-esp-");
        try
        {
            var shim = BootManagerRepair.ShimPathFor("debian");
            BootManagerRepair.LoaderExists(esp.FullName, shim).Should().BeFalse();

            var dir = Path.Join(esp.FullName, "EFI", "debian");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "shimx64.efi"), "not really a binary");

            BootManagerRepair.LoaderExists(esp.FullName, shim).Should().BeTrue();
        }
        finally
        {
            esp.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_stock_path_is_what_a_revert_restores()
    {
        BootManagerRepair.WindowsBootManagerPath
            .Should().Be(@"\EFI\Microsoft\Boot\bootmgfw.efi");
    }
}
