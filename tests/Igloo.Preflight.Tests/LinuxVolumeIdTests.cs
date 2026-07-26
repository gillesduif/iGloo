using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

/// <summary>
/// Pins the ESP-to-partition matching that lets a removal tell two same-family distros apart:
/// the root UUID parsed from an ESP grub.cfg must equal the UUID read from the ext4 superblock of
/// that distro's root partition. Getting this wrong would attribute - and delete - the wrong OS.
/// </summary>
public class LinuxVolumeIdTests
{
    // A real Mint root UUID from the test VM's blkid output.
    private const string MintRootUuid = "de51deb9-06c3-4954-bc3e-cb38b50abb63";

    [Theory]
    [InlineData("search.fs_uuid de51deb9-06c3-4954-bc3e-cb38b50abb63 root")]
    [InlineData("search --no-floppy --fs-uuid --set=root de51deb9-06c3-4954-bc3e-cb38b50abb63")]
    public void Grub_root_fs_uuid_is_parsed_from_both_stub_forms(string grubCfg)
    {
        LinuxVolumeId.ParseGrubRootFsUuid(grubCfg).Should().Be(MintRootUuid);
    }

    [Fact]
    public void Grub_config_without_a_uuid_returns_null()
    {
        LinuxVolumeId.ParseGrubRootFsUuid("set prefix=($root)/boot/grub\nconfigfile $prefix/grub.cfg")
            .Should().BeNull();
    }

    [Fact]
    public void Ext4_superblock_uuid_is_read_and_matches_the_grub_reference()
    {
        var volume = BuildExt4Volume(MintRootUuid);

        var fromSuperblock = LinuxVolumeId.ReadExt4Uuid(volume);
        var fromGrub = LinuxVolumeId.ParseGrubRootFsUuid($"search.fs_uuid {MintRootUuid} root");

        LinuxVolumeId.UuidsMatch(fromSuperblock, fromGrub).Should().BeTrue();
    }

    [Fact]
    public void A_volume_without_the_ext4_magic_returns_null()
    {
        var notExt4 = new byte[LinuxVolumeId.MinReadBytes]; // all zero → no 0xEF53 magic

        LinuxVolumeId.ReadExt4Uuid(notExt4).Should().BeNull();
    }

    /// <summary>Builds a minimal volume image with a valid ext4 superblock carrying <paramref name="uuid"/>.</summary>
    private static byte[] BuildExt4Volume(string uuid)
    {
        var buf = new byte[LinuxVolumeId.MinReadBytes];
        // magic 0xEF53 (little-endian) at superblock+0x38 → 1024 + 56
        buf[1024 + 0x38] = 0x53;
        buf[1024 + 0x39] = 0xEF;
        // 16-byte UUID at superblock+0x68 → 1024 + 104, in display byte order
        var raw = Convert.FromHexString(uuid.Replace("-", "", StringComparison.Ordinal));
        raw.CopyTo(buf, 1024 + 0x68);
        return buf;
    }
}
