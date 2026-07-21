using System.Text;
using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

/// <summary>
/// Characterization tests for the binary formats DirectInstallService emits:
/// the cpio "newc" member appended to installer initrds and the EFI_LOAD_OPTION
/// written to UEFI NVRAM. A malformed byte in either breaks boot in ways that
/// only surface on real firmware, so the layouts are pinned here.
/// </summary>
public class CpioAndEfiFormatTests
{
    // ── cpio newc ────────────────────────────────────────────────────────────

    [Fact]
    public void Cpio_member_starts_with_newc_magic_and_correct_sizes()
    {
        var data = Encoding.ASCII.GetBytes("d-i preseed");
        var cpio = DirectInstallService.BuildNewcCpio("preseed.cfg", data);

        Encoding.ASCII.GetString(cpio, 0, 6).Should().Be("070701");

        // filesize field (8 hex chars) sits at offset 6 + 6*8 = 54.
        var fileSize = Convert.ToUInt32(Encoding.ASCII.GetString(cpio, 54, 8), 16);
        fileSize.Should().Be((uint)data.Length);

        // namesize includes the trailing NUL.
        var nameSize = Convert.ToUInt32(Encoding.ASCII.GetString(cpio, 94, 8), 16);
        nameSize.Should().Be((uint)"preseed.cfg".Length + 1);
    }

    [Fact]
    public void Cpio_archive_ends_with_the_trailer_member()
    {
        var cpio = DirectInstallService.BuildNewcCpio("x", [1]);

        Encoding.ASCII.GetString(cpio).Should().Contain("TRAILER!!!");
    }

    [Fact]
    public void Cpio_total_length_is_4_byte_aligned()
    {
        foreach (var payloadLength in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var cpio = DirectInstallService.BuildNewcCpio("name", new byte[payloadLength]);
            (cpio.Length % 4).Should().Be(0,
                $"the kernel's initramfs parser requires 4-byte padding (payload {payloadLength})");
        }
    }

    [Fact]
    public void Cpio_file_name_is_nul_terminated_after_the_header()
    {
        var cpio = DirectInstallService.BuildNewcCpio("ab", [9]);

        // Header is 110 bytes; name follows immediately.
        Encoding.ASCII.GetString(cpio, 110, 2).Should().Be("ab");
        cpio[112].Should().Be(0);
    }

    // ── EFI_LOAD_OPTION ──────────────────────────────────────────────────────

    private static byte[] LoadOption(string? cmdLine = null) =>
        DirectInstallService.BuildEfiLoadOption(
            partitionNumber: 5, lbaStart: 2048, lbaSize: 1_000_000,
            partGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            efiPath: @"\igloo-boot\shimx64.efi",
            description: "Test Entry",
            cmdLine);

    [Fact]
    public void Load_option_is_active_and_carries_the_ucs2_description()
    {
        var bytes = LoadOption();

        BitConverter.ToUInt32(bytes, 0).Should().Be(1, "LOAD_OPTION_ACTIVE");
        Encoding.Unicode.GetString(bytes, 6, "Test Entry".Length * 2).Should().Be("Test Entry");
    }

    [Fact]
    public void File_path_list_length_covers_exactly_the_device_path()
    {
        var bytes = LoadOption();

        var fplLength = BitConverter.ToUInt16(bytes, 4);
        // Device path = HARDDRIVE node (42) + FILE_PATH node (4 + path bytes) + end node (4).
        var pathBytes = Encoding.Unicode.GetByteCount(@"\igloo-boot\shimx64.efi" + '\0');
        fplLength.Should().Be((ushort)(42 + 4 + pathBytes + 4));

        // With no optional data the option ends right after the device path.
        var descBytes = Encoding.Unicode.GetByteCount("Test Entry" + '\0');
        bytes.Length.Should().Be(4 + 2 + descBytes + fplLength);
    }

    [Fact]
    public void Hard_drive_node_declares_gpt_partition_with_guid_signature()
    {
        var bytes = LoadOption();
        var descBytes = Encoding.Unicode.GetByteCount("Test Entry" + '\0');
        var node = 4 + 2 + descBytes;   // start of the HARDDRIVE node

        bytes[node].Should().Be(0x04, "media device path type");
        bytes[node + 1].Should().Be(0x01, "HARDDRIVE subtype");
        BitConverter.ToUInt16(bytes, node + 2).Should().Be(42, "node length");
        BitConverter.ToUInt32(bytes, node + 4).Should().Be(5, "partition number");
        BitConverter.ToUInt64(bytes, node + 8).Should().Be(2048, "LBA start");
        bytes[node + 40].Should().Be(0x02, "MBRType GPT");
        bytes[node + 41].Should().Be(0x02, "SignatureType GUID");
    }

    [Fact]
    public void Optional_cmdline_is_appended_as_utf16_without_terminator()
    {
        var without = LoadOption();
        var with = LoadOption("initrd=\\igloo-boot\\initrd");

        var extra = Encoding.Unicode.GetByteCount("initrd=\\igloo-boot\\initrd");
        with.Length.Should().Be(without.Length + extra);
        BitConverter.ToUInt16(with, 4).Should().Be(BitConverter.ToUInt16(without, 4),
            "FilePathListLength must NOT include the optional data");
    }

    // ── RoundUpMiB ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1024 * 1024)]
    [InlineData(1024 * 1024, 1024 * 1024)]
    [InlineData(1024 * 1024 + 1, 2 * 1024 * 1024)]
    public void RoundUpMiB_rounds_to_the_next_mebibyte_boundary(long input, long expected)
    {
        DirectInstallService.RoundUpMiB(input).Should().Be(expected);
    }
}
