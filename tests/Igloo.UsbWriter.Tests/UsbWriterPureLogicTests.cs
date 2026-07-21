using System.Text;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.UsbWriter.Tests;

/// <summary>
/// Characterization tests for UsbWriterService's pure helpers: the GRUB kernel-line
/// patch, FAT 8.3 name encoding, the GPT CRC32, sector rounding, and the size
/// pre-flight. Everything else in the class needs a physical drive.
/// </summary>
public class UsbWriterPureLogicTests
{
    // ── PatchGrubCfgContent ──────────────────────────────────────────────────

    [Fact]
    public void Kernel_lines_get_nomodeset_and_lose_rd_live_check()
    {
        var cfg = "menuentry 'Install' {\n" +
                  "    linuxefi /images/pxeboot/vmlinuz root=live rd.live.check quiet\n" +
                  "    initrdefi /images/pxeboot/initrd.img\n" +
                  "}\n";

        var patched = UsbWriterService.PatchGrubCfgContent(cfg);

        patched.Should().Contain("linuxefi /images/pxeboot/vmlinuz root=live quiet nomodeset");
        patched.Should().NotContain("rd.live.check",
            "dracut's getarg is a PRESENCE check; even =0 triggers the media check");
        patched.Should().Contain("initrdefi /images/pxeboot/initrd.img",
            "initrd lines must be untouched");
    }

    [Fact]
    public void Patch_is_idempotent_across_reruns()
    {
        var cfg = "  linux /vmlinuz rd.live.check=1 nomodeset quiet\n";

        var once = UsbWriterService.PatchGrubCfgContent(cfg);
        var twice = UsbWriterService.PatchGrubCfgContent(once);

        twice.Should().Be(once);
        System.Text.RegularExpressions.Regex.Matches(twice, "nomodeset").Count.Should().Be(1);
    }

    [Fact]
    public void Non_kernel_lines_are_left_alone()
    {
        var cfg = "set default=0\nset timeout=5\nsearch --label OEMDRV\n";

        UsbWriterService.PatchGrubCfgContent(cfg).Should().Be(cfg);
    }

    [Fact]
    public void Crlf_line_endings_are_preserved()
    {
        var cfg = "\tlinux /vmlinuz quiet\r\nboot\r\n";

        var patched = UsbWriterService.PatchGrubCfgContent(cfg);

        patched.Should().Be("\tlinux /vmlinuz quiet nomodeset\r\nboot\r\n");
    }

    // ── Fat32Make83 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("grub.cfg", "GRUB    CFG")]
    [InlineData("EFI", "EFI        ")]
    [InlineData("verylongname.extension", "VERYLONGEXT")]
    public void Names_encode_to_11_byte_space_padded_8_3(string name, string expected)
    {
        Encoding.ASCII.GetString(UsbWriterService.Fat32Make83(name)).Should().Be(expected);
    }

    // ── GptCrc32 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Crc32_matches_the_ieee_check_value()
    {
        // The canonical CRC-32/IEEE test vector: "123456789" → 0xCBF43926.
        var data = Encoding.ASCII.GetBytes("123456789");

        UsbWriterService.GptCrc32(data, data.Length).Should().Be(0xCBF43926u);
    }

    // ── RoundUpToSector ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 512)]
    [InlineData(512, 512)]
    [InlineData(513, 1024)]
    public void Byte_counts_round_up_to_whole_sectors(int bytes, int expected)
    {
        UsbWriterService.RoundUpToSector(bytes).Should().Be(expected);
    }

    // ── ValidateFit ──────────────────────────────────────────────────────────

    private static UsbDriveInfo Drive(long sizeBytes) =>
        new(1, "Test Stick", sizeBytes, @"\\.\PHYSICALDRIVE1");

    [Fact]
    public void Too_small_drive_fails_fast_with_an_actionable_message()
    {
        var act = () => UsbWriterService.ValidateFit(
            Drive(4L * 1024 * 1024 * 1024), isoSize: 5L * 1024 * 1024 * 1024, partSizeMb: 512);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
    }

    [Fact]
    public void Big_enough_drive_passes()
    {
        var act = () => UsbWriterService.ValidateFit(
            Drive(16L * 1024 * 1024 * 1024), isoSize: 3L * 1024 * 1024 * 1024, partSizeMb: 512);

        act.Should().NotThrow();
    }

    [Fact]
    public void Unknown_drive_size_skips_the_check()
    {
        var act = () => UsbWriterService.ValidateFit(
            Drive(0), isoSize: long.MaxValue / 2, partSizeMb: 512);

        act.Should().NotThrow("WMI sometimes cannot report a size; the write attempt decides");
    }
}
