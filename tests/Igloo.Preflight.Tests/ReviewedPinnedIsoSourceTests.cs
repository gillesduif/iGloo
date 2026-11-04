using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedPinnedIsoSourceTests
{
    [PinnedIsoFact]
    public void PinnedCurrentDebianLiveIsoExposesTheRequiredEfiMembers()
    {
        var path = Environment.GetEnvironmentVariable("IGLOO_DEBIAN_PINNED_ISO") ??
            throw new InvalidOperationException("The current Debian ISO path was not supplied.");

        const string pin = "e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17";
        using var source = new ReviewedPinnedIsoSource(path,
            new FileInfo(path).Length, pin);
        var shimPath = source.FirstAvailable(
            ["EFI/debian/shimx64.efi", "EFI/BOOT/shimx64.efi",
                "EFI/BOOT/BOOTX64.EFI"]);
        var grubPath = source.FirstAvailable(
            ["EFI/debian/grubx64.efi", "EFI/BOOT/grubx64.efi"]);
        Assert.NotNull(shimPath);
        Assert.NotNull(grubPath);
        var selected = ReviewedSeedFileWriter.FindInstallerEfiPair(source);
        Assert.Equal("EFI/BOOT/BOOTX64.EFI", selected.Shim);
        Assert.Equal("EFI/BOOT/grubx64.efi", selected.Grub);
        var shim = source.Extract(shimPath);
        var grub = source.Extract(grubPath);
        try
        {
            Assert.True(shim.SizeBytes > 0);
            Assert.True(grub.SizeBytes > 0);
            Assert.Equal(shim.Sha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shim.TemporaryPath))));
            Assert.Equal(grub.Sha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(grub.TemporaryPath))));
            Assert.Equal((shim.SizeBytes, shim.Sha256), source.HashMember(selected.Shim));
            Assert.Equal((grub.SizeBytes, grub.Sha256), source.HashMember(selected.Grub));
            Assert.Equal("E103C5D02657879F141B086ED703584D28BB9E2721B423333BE085EE1B4D397B",
                shim.Sha256);
            Assert.Equal("B102E12508C773796D102D58F81BB8A00357C01DAA09092AE1D2B1D1702BC310",
                grub.Sha256);
            Console.WriteLine($"Debian signed ISO shim={shimPath} bytes={shim.SizeBytes} sha256={shim.Sha256}");
            Console.WriteLine($"Debian signed ISO grub={grubPath} bytes={grub.SizeBytes} sha256={grub.Sha256}");
            source.VerifyPin();
        }
        finally
        {
            File.Delete(shim.TemporaryPath);
            File.Delete(grub.TemporaryPath);
        }
    }

    [Fact]
    public async Task ExtractedBootAndStage2BytesComeFromThePinnedIsoFile()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-pinned-iso-test-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var isoPath = Path.Join(root, "reviewed.iso");
        try
        {
            var image = MakeIso();
            await File.WriteAllBytesAsync(isoPath, image);
            var pin = Convert.ToHexString(SHA256.HashData(image));
            using (var source = new ReviewedPinnedIsoSource(isoPath, image.Length, pin))
            {
                Assert.Equal("EFI/BOOT/BOOTX64.EFI", source.FirstAvailable(
                    ["EFI/debian/shimx64.efi", "EFI/BOOT/BOOTX64.EFI"]));
                var shim = source.Extract("EFI/BOOT/BOOTX64.EFI");
                var grub = source.Extract("EFI/BOOT/grubx64.efi");
                var stage2 = source.Extract("images/install.img", 13);
                try
                {
                    Assert.Equal("signed shim", await File.ReadAllTextAsync(shim.TemporaryPath));
                    Assert.Equal("signed grub", await File.ReadAllTextAsync(grub.TemporaryPath));
                    Assert.Equal("stage2 squash", await File.ReadAllTextAsync(stage2.TemporaryPath));
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(
                        Encoding.ASCII.GetBytes("signed shim"))), shim.Sha256);
                    Assert.Throws<InvalidDataException>(() =>
                        source.Extract("images/install.img", 14));
                    Assert.Throws<InvalidDataException>(() =>
                        source.Extract("EFI/BOOT/../BOOTX64.EFI"));
                    source.VerifyPin();
                }
                finally
                {
                    File.Delete(shim.TemporaryPath);
                    File.Delete(grub.TemporaryPath);
                    File.Delete(stage2.TemporaryPath);
                }
            }
            image[24 * 2048] ^= 1;
            await File.WriteAllBytesAsync(isoPath, image);
            Assert.Throws<InvalidDataException>(() =>
                new ReviewedPinnedIsoSource(isoPath, image.Length, pin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ShimAndGrubFromDifferentDirectoriesCannotFormAnInstallerPair()
    {
        var path = Path.Join(Path.GetTempPath(), "igloo-mismatched-efi-" +
            Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            var image = MakeIso(grubInSeparateDirectory: true);
            await File.WriteAllBytesAsync(path, image);
            using var source = new ReviewedPinnedIsoSource(path, image.Length,
                Convert.ToHexString(SHA256.HashData(image)));
            Assert.Equal("EFI/BOOT/BOOTX64.EFI", source.FirstAvailable(
                ["EFI/BOOT/BOOTX64.EFI"]));
            Assert.Equal("EFI/debian/grubx64.efi", source.FirstAvailable(
                ["EFI/debian/grubx64.efi"]));
            Assert.Null(source.FirstAvailablePair(
                [("EFI/debian/shimx64.efi", "EFI/debian/grubx64.efi"),
                    ("EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/grubx64.efi")]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadOnlyMediaInspectorMeasuresPinnedIsoMembersAndExternalHdMedia()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-media-inspect-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = MakeIso();
            var isoPath = Path.Join(root, "installer.iso");
            await File.WriteAllBytesAsync(isoPath, image);
            var isoPin = Convert.ToHexString(SHA256.HashData(image));
            var isoBoot = new InstallerBootSpec
            {
                MenuTitle = "Fixture", KernelCmdline = "fixture", IsoSha256 = isoPin,
                KernelIsoPaths = ["EFI/BOOT/BOOTX64.EFI"],
                InitrdIsoPaths = ["EFI/BOOT/grubx64.efi"],
                ExtraIsoFiles = [new("images/install.img", "images/install.img", true)],
            };
            var measured = ReviewedMediaInspector.Inspect(isoPath, isoBoot);
            Assert.Equal((ulong)image.Length, measured.FullIsoBytes);
            Assert.Equal(11UL, measured.KernelBytes);
            Assert.Equal(11UL, measured.InitrdBytes);
            Assert.Equal(13UL, measured.Stage2Bytes);
            Assert.Throws<InvalidDataException>(() => ReviewedMediaInspector.Inspect(isoPath,
                isoBoot with { ExtraIsoFiles = [new("images/missing.img", "missing.img", true)] }));

            var kernelPath = Path.Join(root, "hd-vmlinuz");
            var initrdPath = Path.Join(root, "hd-initrd.gz");
            var kernel = Encoding.ASCII.GetBytes("external kernel");
            var initrd = Encoding.ASCII.GetBytes("external initrd");
            await File.WriteAllBytesAsync(kernelPath, kernel);
            await File.WriteAllBytesAsync(initrdPath, initrd);
            var hdBoot = isoBoot with
            {
                KernelUrl = new Uri("https://example.invalid/vmlinuz"),
                InitrdUrl = new Uri("https://example.invalid/initrd.gz"),
                KernelSha256 = Convert.ToHexString(SHA256.HashData(kernel)),
                InitrdSha256 = Convert.ToHexString(SHA256.HashData(initrd)),
                ExtraIsoFiles = [],
            };
            Assert.Throws<InvalidDataException>(() => ReviewedMediaInspector.Inspect(isoPath, hdBoot));
            measured = ReviewedMediaInspector.Inspect(isoPath, hdBoot, kernelPath, initrdPath);
            Assert.Equal((ulong)kernel.Length, measured.KernelBytes);
            Assert.Equal((ulong)initrd.Length, measured.InitrdBytes);
            Assert.Equal(0UL, measured.Stage2Bytes);
            Assert.Throws<InvalidDataException>(() => ReviewedMediaInspector.Inspect(isoPath,
                hdBoot with { KernelSha256 = new string('0', 64) },
                kernelPath, initrdPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static byte[] MakeIso(bool grubInSeparateDirectory = false)
    {
        const int block = 2048;
        var iso = new byte[32 * block];
        var pvd = 16 * block;
        iso[pvd] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(pvd + 1));
        iso[pvd + 6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(iso.AsSpan(pvd + 128), block);
        BinaryPrimitives.WriteUInt16BigEndian(iso.AsSpan(pvd + 130), block);
        Record(iso, pvd + 156, [0], 20, block, directory: true);

        var root = 20 * block;
        var offset = Record(iso, root, "EFI"u8, 21, block, directory: true);
        Record(iso, root + offset, "IMAGES"u8, 23, block, directory: true);
        var efiOffset = Record(iso, 21 * block, "BOOT"u8, 22, block, directory: true);
        if (grubInSeparateDirectory)
            Record(iso, 21 * block + efiOffset, "DEBIAN"u8, 27, block, directory: true);
        var efi = 22 * block;
        offset = Record(iso, efi, "BOOTX64.EFI;1"u8, 24, 11, directory: false);
        Record(iso, grubInSeparateDirectory ? 27 * block : efi + offset,
            "GRUBX64.EFI;1"u8, 25, 11, directory: false);
        Record(iso, 23 * block, "INSTALL.IMG;1"u8, 26, 13, directory: false);
        Encoding.ASCII.GetBytes("signed shim").CopyTo(iso.AsSpan(24 * block));
        Encoding.ASCII.GetBytes("signed grub").CopyTo(iso.AsSpan(25 * block));
        Encoding.ASCII.GetBytes("stage2 squash").CopyTo(iso.AsSpan(26 * block));
        return iso;
    }

    private static int Record(byte[] iso, int offset, ReadOnlySpan<byte> name,
        uint lba, int bytes, bool directory)
    {
        var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        iso[offset] = checked((byte)length);
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(offset + 2), lba);
        BinaryPrimitives.WriteUInt32BigEndian(iso.AsSpan(offset + 6), lba);
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(offset + 10),
            checked((uint)bytes));
        BinaryPrimitives.WriteUInt32BigEndian(iso.AsSpan(offset + 14),
            checked((uint)bytes));
        iso[offset + 25] = directory ? (byte)2 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(iso.AsSpan(offset + 28), 1);
        BinaryPrimitives.WriteUInt16BigEndian(iso.AsSpan(offset + 30), 1);
        iso[offset + 32] = checked((byte)name.Length);
        name.CopyTo(iso.AsSpan(offset + 33));
        return length;
    }

    private sealed class PinnedIsoFactAttribute : FactAttribute
    {
        public PinnedIsoFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("IGLOO_DEBIAN_PINNED_ISO")))
                Skip = "Read-only pinned ISO check requires IGLOO_DEBIAN_PINNED_ISO.";
        }
    }
}
