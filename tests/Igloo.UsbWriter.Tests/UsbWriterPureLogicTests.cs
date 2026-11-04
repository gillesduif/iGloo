using System.Text;
using System.Security.Cryptography;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.UsbWriter.Tests;

public class UsbWriterPureLogicTests
{
    [Fact]
    public async Task Usb_source_is_rehashed_and_rewound_before_any_raw_write()
    {
        var acquiredBytes = Encoding.ASCII.GetBytes("verified installer image");
        var hash = Convert.ToHexString(SHA256.HashData(acquiredBytes));
        using var source = new MemoryStream(acquiredBytes);
        source.Position = source.Length;

        await UsbWriterService.VerifyIsoAsync(source, hash);
        source.Position.Should().Be(0);

        source.SetLength(source.Length - 1);
        var verifyChangedSource = () => UsbWriterService.VerifyIsoAsync(source, hash);
        await verifyChangedSource.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*changed after acquisition*");
        var verifyMissingIdentity = () => UsbWriterService.VerifyIsoAsync(source, "");
        await verifyMissingIdentity.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Usb_readback_verifies_only_written_image_bytes_and_detects_corruption()
    {
        var image = Encoding.ASCII.GetBytes("installer image bytes");
        var hash = Convert.ToHexString(SHA256.HashData(image));
        byte[] diskBytes = [.. image, 42, 43];
        using var written = new MemoryStream(diskBytes, writable: true);
        await UsbWriterService.VerifyWrittenImageAsync(written, image.Length, hash);
        written.Position = 0;
        diskBytes[0] ^= 1;
        var verifyCorrupt = () => UsbWriterService.VerifyWrittenImageAsync(written,
            image.Length, hash);
        await verifyCorrupt.Should().ThrowAsync<IOException>()
            .WithMessage("*differs from the acquired image*");
    }

    //   PatchGrubCfgContent                          

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

    [Fact]
    public void Fedora_unattended_patch_selects_install_entry_and_loads_staged_kickstart()
    {
        var cfg = "set default=\"1\"\n" +
                  "menuentry 'Install Fedora 44' {\n" +
                  " linux /images/pxeboot/vmlinuz inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-44 quiet\n}\n" +
                  "menuentry 'Test this media & install Fedora 44' {\n" +
                  " linux /images/pxeboot/vmlinuz inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-44 rd.live.check quiet\n}\n" +
                  "menuentry 'Rescue a Fedora system' {\n" +
                  " linux /images/pxeboot/vmlinuz inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-44 inst.rescue quiet\n}\n";

        var patched = UsbWriterService.PatchGrubCfgContent(cfg, unattendedKickstart: true);

        patched.Should().Contain("set default=\"0\"");
        patched.Should().Contain("inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-44 quiet inst.ks=hd:LABEL=OEMDRV:/ks.cfg nomodeset");
        patched.Split("inst.ks=", StringSplitOptions.None).Should().HaveCount(2);
        patched.Should().Contain("inst.rescue quiet nomodeset");
        UsbWriterService.PatchGrubCfgContent(patched, unattendedKickstart: true)
            .Should().Be(patched);
    }

    [Fact]
    public void Fedora_unattended_patch_rejects_an_unknown_menu()
    {
        var cfg = "set default=1\nmenuentry 'Rescue Fedora' {\n linux /vmlinuz inst.rescue\n}\n";
        var patch = () => UsbWriterService.PatchGrubCfgContent(cfg, unattendedKickstart: true);
        patch.Should().Throw<InvalidDataException>();
    }

    //   Fat32Make83                              

    [Theory]
    [InlineData("grub.cfg", "GRUB    CFG")]
    [InlineData("EFI", "EFI        ")]
    [InlineData("verylongname.extension", "VERYLONGEXT")]
    public void Names_encode_to_11_byte_space_padded_8_3(string name, string expected)
    {
        Encoding.ASCII.GetString(UsbWriterService.Fat32Make83(name)).Should().Be(expected);
    }

    //   GptCrc32                               ─

    [Fact]
    public void Crc32_matches_the_ieee_check_value()
    {
        // The canonical CRC-32/IEEE test vector: "123456789" → 0xCBF43926.
        var data = Encoding.ASCII.GetBytes("123456789");

        UsbWriterService.GptCrc32(data, data.Length).Should().Be(0xCBF43926u);
    }

    [Fact]
    public async Task Fedora_sized_gpt_table_is_checked_before_a_raw_write()
    {
        using var valid = FedoraSizedGptImage();
        await UsbWriterService.RequireGptInstallerImageAsync(valid);
        valid.Position.Should().Be(0);

        using var overlapping = FedoraSizedGptImage(overlap: true);
        var rejectOverlap = () => UsbWriterService.RequireGptInstallerImageAsync(overlapping);
        await rejectOverlap.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*overlap*");

        using var shifted = FedoraSizedGptImage(entryLba: 12);
        var rejectShifted = () => UsbWriterService.RequireGptInstallerImageAsync(shifted);
        await rejectShifted.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*unsupported*");
    }

    private static MemoryStream FedoraSizedGptImage(bool overlap = false, ulong entryLba = 2)
    {
        const int sectors = 200;
        const int entryCount = 248;
        const int entrySize = 128;
        const int tableSectors = entryCount * entrySize / 512;
        var image = new byte[sectors * 512];
        var entries = new byte[entryCount * entrySize];
        entries[0] = 0x28;
        BitConverter.TryWriteBytes(entries.AsSpan(32, 8), 64UL);
        BitConverter.TryWriteBytes(entries.AsSpan(40, 8), 100UL);
        entries[128] = 0x28;
        BitConverter.TryWriteBytes(entries.AsSpan(128 + 32, 8), overlap ? 100UL : 101UL);
        BitConverter.TryWriteBytes(entries.AsSpan(128 + 40, 8), 120UL);
        entries.CopyTo(image, checked((int)entryLba * 512));
        entries.CopyTo(image, (sectors - 1 - tableSectors) * 512);

        var header = image.AsSpan(512, 512);
        "EFI PART"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header[8..12], 0x00010000u);
        BitConverter.TryWriteBytes(header[12..16], 92u);
        BitConverter.TryWriteBytes(header[24..32], 1UL);
        BitConverter.TryWriteBytes(header[32..40], (ulong)sectors - 1);
        BitConverter.TryWriteBytes(header[40..48], 64UL);
        BitConverter.TryWriteBytes(header[48..56], (ulong)(sectors - 2 - tableSectors));
        BitConverter.TryWriteBytes(header[72..80], entryLba);
        BitConverter.TryWriteBytes(header[80..84], (uint)entryCount);
        BitConverter.TryWriteBytes(header[84..88], (uint)entrySize);
        BitConverter.TryWriteBytes(header[88..92], UsbWriterService.GptCrc32(entries, entries.Length));
        var backup = image.AsSpan((sectors - 1) * 512, 512);
        header.CopyTo(backup);
        BitConverter.TryWriteBytes(backup[24..32], (ulong)sectors - 1);
        BitConverter.TryWriteBytes(backup[32..40], 1UL);
        BitConverter.TryWriteBytes(backup[72..80], (ulong)(sectors - 1 - tableSectors));
        BitConverter.TryWriteBytes(backup[16..20], UsbWriterService.GptCrc32(backup.ToArray(), 92));
        BitConverter.TryWriteBytes(header[16..20], UsbWriterService.GptCrc32(header.ToArray(), 92));
        return new MemoryStream(image, writable: false);
    }

    //   RoundUpToSector                            

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 512)]
    [InlineData(512, 512)]
    [InlineData(513, 1024)]
    public void Byte_counts_round_up_to_whole_sectors(int bytes, int expected)
    {
        UsbWriterService.RoundUpToSector(bytes).Should().Be(expected);
    }

    //   ValidateFit                              

    private static UsbDriveInfo Drive(long sizeBytes) =>
        new(1, "Test Stick", sizeBytes, @"\\.\PHYSICALDRIVE1");

    [Fact]
    public void Usb_target_revalidation_requires_the_same_provider_identity()
    {
        var selected = Drive(16L << 30) with { PnpDeviceId = @"USBSTOR\DISK&VEN_TEST\DEVICE-123",
            LogicalSectorBytes = 512 };
        UsbWriterService.SameObservedDrive(selected, selected).Should().BeTrue();
        UsbWriterService.SameObservedDrive(selected,
            selected with { PnpDeviceId = @"USBSTOR\DISK&VEN_TEST\DEVICE-999" }).Should().BeFalse();
        UsbWriterService.SameObservedDrive(selected,
            selected with { SizeBytes = 8L << 30 }).Should().BeFalse();
        UsbWriterService.SameObservedDrive(selected,
            selected with { DriveIndex = 2, DeviceId = @"\\.\PHYSICALDRIVE2" }).Should().BeFalse();
        UsbWriterService.SameObservedDrive(selected with { PnpDeviceId = null }, selected)
            .Should().BeFalse();
        UsbWriterService.SameObservedDrive(selected,
            selected with { LogicalSectorBytes = 4096 }).Should().BeFalse();
        UsbWriterService.SameObservedDrive(selected with { LogicalSectorBytes = null }, selected)
            .Should().BeFalse();
        UsbWriterService.UniqueObservedDrive(selected, [selected]).Should().BeTrue();
        UsbWriterService.UniqueObservedDrive(selected,
            [selected, selected with { DriveIndex = 2, DeviceId = @"\\.\PHYSICALDRIVE2" }])
            .Should().BeFalse("two USB devices sharing a provider PNP identity are ambiguous");
    }

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
    public void Unknown_drive_size_blocks_the_write()
    {
        var act = () => UsbWriterService.ValidateFit(
            Drive(0), isoSize: long.MaxValue / 2, partSizeMb: 512);

        act.Should().Throw<InvalidOperationException>().WithMessage("*size is unavailable*");
    }

    [Fact]
    public void Unrepresentable_required_capacity_blocks_the_write()
    {
        var act = () => UsbWriterService.ValidateFit(
            Drive(long.MaxValue), isoSize: long.MaxValue, partSizeMb: 512);

        act.Should().Throw<InvalidOperationException>().WithMessage("*size is invalid*");
    }

    [Fact]
    public void Fat32_single_file_limit_is_checked_before_raw_write()
    {
        Action atLimit = () => UsbWriterService.ValidateFat32FileSize(uint.MaxValue);
        Action aboveLimit = () => UsbWriterService.ValidateFat32FileSize((long)uint.MaxValue + 1);

        atLimit.Should().NotThrow();
        aboveLimit.Should().Throw<InvalidOperationException>().WithMessage("*FAT32*");
    }

    [Fact]
    public async Task Staging_copy_preserves_empty_selected_folders_and_browser_profiles()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-usb-copy-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Join(root, "staging");
        var destination = Path.Join(root, "seed");
        Directory.CreateDirectory(Path.Join(staging, "files", "Documents"));
        Directory.CreateDirectory(Path.Join(staging, "browser-profiles", "AppData", "Firefox"));
        await File.WriteAllTextAsync(Path.Join(staging, "browser-profiles", "AppData", "Firefox",
            "profiles.ini"), "selected browser");
        try
        {
            await UsbWriterService.CopyStagingFilesAsync(destination, staging,
                "selected browser".Length, progress: null, ct: CancellationToken.None);

            Directory.Exists(Path.Join(destination, "files", "Documents")).Should().BeTrue();
            (await File.ReadAllTextAsync(Path.Join(destination, "browser-profiles", "AppData",
                "Firefox", "profiles.ini"))).Should().Be("selected browser");
        }
        finally
        {
            Path.GetFileName(root).Should().StartWith("igloo-usb-copy-");
            Directory.Delete(root, recursive: true);
        }
    }

    //   ResolveDestinationPath

    [Theory]
    [InlineData(@"a.txt", @"E:\stage\a.txt")]
    [InlineData(@"sub\a.txt", @"E:\stage\sub\a.txt")]
    [InlineData(@"sub\..\a.txt", @"E:\stage\a.txt")]
    public void Relative_paths_resolve_inside_the_destination(string relative, string expected)
    {
        UsbWriterService.ResolveDestinationPath(@"E:\stage", relative)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(@"D:\other\evil.txt")]
    [InlineData(@"\evil.txt")]
    [InlineData(@"..\..\evil.txt")]
    [InlineData(@"sub\..\..\evil.txt")]
    public void Escaping_paths_are_refused(string relative)
    {
        var act = () => UsbWriterService.ResolveDestinationPath(@"E:\stage", relative);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Refusing to write outside*");
    }

    [Fact]
    public void A_sibling_directory_sharing_the_prefix_is_refused()
    {
        var act = () => UsbWriterService.ResolveDestinationPath(@"E:\stage", @"..\stage-evil\a.txt");

        act.Should().Throw<InvalidOperationException>();
    }
}
