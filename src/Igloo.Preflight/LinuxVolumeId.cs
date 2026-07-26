using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace Igloo.Preflight;

/// <summary>
/// Maps an ESP loader folder to the exact partition it boots, so a removal targets the right OS
/// even when two same-family distros share a disk. An ESP <c>\EFI\&lt;distro&gt;\grub.cfg</c> names
/// its root by filesystem UUID; that same UUID sits in the root partition's ext4 superblock. Reading
/// both and matching them attributes each partition to its distro without guessing.
///
/// Debian-family only: Ubuntu/Debian/Mint put root on a plain ext4 partition, so its superblock is
/// at the partition start. Fedora-family root lives inside LVM, so this does not apply there (the
/// LVM GPT type is used instead).
/// </summary>
internal static class LinuxVolumeId
{
    // grub.cfg stubs reference the root by filesystem UUID, e.g.
    //   search.fs_uuid de51deb9-06c3-4954-bc3e-cb38b50abb63 root
    //   search --fs-uuid --set=root de51deb9-06c3-4954-bc3e-cb38b50abb63
    private static readonly Regex FsUuidRegex = new(
        @"fs[_-]uuid\D+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The root filesystem UUID an ESP grub.cfg stub points at, or null if none is present.</summary>
    public static string? ParseGrubRootFsUuid(string grubCfg)
    {
        ArgumentNullException.ThrowIfNull(grubCfg);
        var match = FsUuidRegex.Match(grubCfg);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ext4 superblock: begins 1024 bytes into the volume; the 0xEF53 magic is at +0x38 and the
    // 16-byte volume UUID at +0x68, stored in display order (the form blkid and grub print).
    private const int SuperblockOffset = 1024;
    private const int MagicOffset = 0x38;
    private const int UuidOffset = 0x68;
    private const ushort Ext4Magic = 0xEF53;

    /// <summary>Bytes from the volume start needed to read the UUID (read at least this many).</summary>
    public const int MinReadBytes = SuperblockOffset + UuidOffset + 16;

    /// <summary>
    /// The filesystem UUID from an ext4/3/2 superblock at the start of a volume, or null when the
    /// ext magic is absent (not an ext-family filesystem - e.g. an LVM or swap partition).
    /// </summary>
    public static string? ReadExt4Uuid(ReadOnlySpan<byte> volumeStart)
    {
        if (volumeStart.Length < MinReadBytes)
            return null;

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(
            volumeStart.Slice(SuperblockOffset + MagicOffset, 2));
        if (magic != Ext4Magic)
            return null;

        var u = volumeStart.Slice(SuperblockOffset + UuidOffset, 16);
        return string.Join('-',
            Convert.ToHexString(u[..4]), Convert.ToHexString(u[4..6]), Convert.ToHexString(u[6..8]),
            Convert.ToHexString(u[8..10]), Convert.ToHexString(u[10..16]));
    }

    /// <summary>UUID equality that ignores case and dashes so grub and superblock forms compare equal.</summary>
    public static bool UuidsMatch(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.Replace("-", "", StringComparison.Ordinal),
                      b.Replace("-", "", StringComparison.Ordinal),
                      StringComparison.OrdinalIgnoreCase);
}
