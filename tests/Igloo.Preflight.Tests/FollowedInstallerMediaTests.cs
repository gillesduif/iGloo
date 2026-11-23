using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class FollowedInstallerMediaTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "igloo-followed-media-" + Guid.NewGuid().ToString("N"));

    public FollowedInstallerMediaTests() => Directory.CreateDirectory(Path.Join(_directory, "image", "live"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // A setup header as the kernel's boot protocol lays it out: the magic at 0x202, the version pointer at 0x20E.
    private static byte[] KernelImage(string? version, ushort pointer = 0x400, bool magic = true)
    {
        var image = new byte[0x200 + pointer + 256];
        if (magic)
            "HdrS"u8.CopyTo(image.AsSpan(0x202));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x20E), version is null ? (ushort)0 : pointer);
        if (version is not null)
            Encoding.ASCII.GetBytes(version).CopyTo(image.AsSpan(0x200 + pointer));
        return image;
    }

    private static string? Release(byte[] image)
    {
        using var stream = new MemoryStream(image);
        return LinuxKernelImage.ReadRelease(stream);
    }

    [Fact]
    public void The_release_is_the_first_word_of_the_version_the_setup_header_points_at()
    {
        var debian = KernelImage("6.12.117+deb13-amd64 (debian-kernel@lists.debian.org) #1 SMP PREEMPT_DYNAMIC Debian 6.12.117-1 (2026-11-12)\0");

        Assert.Equal("6.12.117+deb13-amd64", Release(debian));
        Assert.Equal("6.19.10-300.fc44.x86_64", Release(KernelImage("6.19.10-300.fc44.x86_64\0")));
    }

    [Fact]
    public void A_file_that_is_no_kernel_image_names_no_release()
    {
        Assert.Null(Release(KernelImage("6.12.117+deb13-amd64 (x)\0", magic: false)));
        Assert.Null(Release(KernelImage(null)));
        Assert.Null(Release(new byte[0x100]));
        Assert.Null(Release(KernelImage("6.12.117+deb13-amd64 (x)\0")[..0x500]));
        Assert.Null(Release(KernelImage(" 6.12.117\0")));
        Assert.Null(Release(KernelImage(new string('6', 200))));
        // A release goes into a message and is compared; nothing but the characters of a version is taken.
        Assert.Null(Release(KernelImage("6.12/../x \0")));
        Assert.Null(Release(KernelImage("6.12\n7 \0")));
    }

    private DirectInstallService.PinnedInstallerFile InstallerKernel(string version)
    {
        var path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".bin");
        var bytes = KernelImage(version);
        File.WriteAllBytes(path, bytes);
        return new DirectInstallService.PinnedInstallerFile(path, Convert.ToHexString(SHA256.HashData(bytes)), CancellationToken.None);
    }

    private static InstallerBootSpec Followed() => new()
    {
        MenuTitle = "Install Fixture (Igloo)",
        KernelCmdline = "auto=true ---",
        KernelIsoPaths = ["live/vmlinuz"],
        InitrdIsoPaths = ["live/initrd.img"],
        KernelUrl = new Uri("https://example.invalid/dists/x/images/vmlinuz"),
        InitrdUrl = new Uri("https://example.invalid/dists/x/images/initrd.gz"),
        KernelSha256 = new string('a', 64),
        InitrdSha256 = new string('b', 64),
        MediaIndex = new InstallerMediaIndex
        {
            SignedIndexUrl = new Uri("https://example.invalid/dists/x/InRelease"),
            IndexIdentityLine = "Codename: x",
            SigningKeyUrl = new Uri("https://example.invalid/key.asc"),
            SigningKeyFingerprint = new string('A', 40),
            ChecksumFileUrl = new Uri("https://example.invalid/dists/x/images/SHA256SUMS"),
            ImageKernelPath = "install/vmlinuz",
        },
    };

    // An image as the installer needs it: its own installer kernel, a live kernel of another release, and its boot files.
    private string Image(string? installerKernelVersion)
    {
        var root = Path.Join(_directory, "image");
        Directory.CreateDirectory(Path.Join(root, "install"));
        Directory.CreateDirectory(Path.Join(root, "EFI", "boot"));
        File.WriteAllBytes(Path.Join(root, "EFI", "boot", "bootx64.efi"), [1]);
        File.WriteAllBytes(Path.Join(root, "EFI", "boot", "grubx64.efi"), [2]);
        File.WriteAllBytes(Path.Join(root, "live", "vmlinuz"), KernelImage("9.9.9-live (x) #1\0"));
        if (installerKernelVersion is not null)
            File.WriteAllBytes(Path.Join(root, "install", "vmlinuz"), KernelImage(installerKernelVersion));
        return root;
    }

    [Fact]
    public void An_installer_kernel_of_the_release_of_the_images_kernel_is_taken_and_stays_readable()
    {
        using var kernel = InstallerKernel("6.12.117+deb13-amd64 (debian-kernel@lists.debian.org) #1 SMP\0");

        DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (debian-kernel@lists.debian.org) #1 SMP\0"), Followed(), kernel);

        // The same locked file is copied to the installer partition afterwards, from its first byte.
        var copy = Path.Join(_directory, "copy.bin");
        kernel.CopyTo(copy, null, CancellationToken.None);
        Assert.Equal(kernel.Length, new FileInfo(copy).Length);
        Assert.Equal("HdrS", Encoding.ASCII.GetString(File.ReadAllBytes(copy), 0x202, 4));
    }

    [Fact]
    public void An_installer_kernel_of_another_release_than_the_image_is_refused_with_both_named()
    {
        using var kernel = InstallerKernel("6.12.107+deb13-amd64 (x) #1\0");

        var refusal = Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (x) #1\0"), Followed(), kernel));

        Assert.Contains("kernel 6.12.107+deb13-amd64", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("kernel 6.12.117+deb13-amd64", refusal.Message, StringComparison.Ordinal);
        // Going back a step does not fetch the image again in this wizard; starting it again does.
        Assert.Contains("Restart iGloo Community", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Go back", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_systems_kernel_is_not_what_the_installer_kernel_is_compared_with()
    {
        using var kernel = InstallerKernel("9.9.9-live (x) #1\0");

        Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (x) #1\0"), Followed(), kernel));
    }

    [Fact]
    public void An_image_whose_installer_kernel_cannot_be_read_is_refused_rather_than_assumed_to_fit()
    {
        using var kernel = InstallerKernel("6.12.117+deb13-amd64 (x) #1\0");

        var absent = Assert.Throws<InstallationRefusedException>(() =>
            DirectInstallService.RequireImageOfInstallerKernel(Image(null), Followed(), kernel));
        Assert.Equal("Installer version could not be verified", absent.Heading);
        Assert.Contains("Image kernel: unreadable", absent.InnerException!.Message, StringComparison.Ordinal);
        // Waiting does not change this; the instruction is the one for a layout iGloo does not know.
        Assert.Contains("Update iGloo Community", absent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("few hours", absent.Message, StringComparison.Ordinal);

        File.WriteAllBytes(Path.Join(_directory, "image", "install", "vmlinuz"), new byte[4096]);
        Assert.Throws<InstallationRefusedException>(() =>
            DirectInstallService.RequireImageOfInstallerKernel(Path.Join(_directory, "image"), Followed(), kernel));
    }

    private DirectInstallService.PinnedInstallerFile Blob()
    {
        var path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".blob");
        var bytes = new byte[8192];
        File.WriteAllBytes(path, bytes);
        return new DirectInstallService.PinnedInstallerFile(path, Convert.ToHexString(SHA256.HashData(bytes)), CancellationToken.None);
    }

    [Fact]
    public void An_installer_kernel_that_names_no_release_is_refused()
    {
        using var kernel = Blob();

        var refusal = Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (x) #1\0"), Followed(), kernel));

        Assert.Contains("Installer kernel: unreadable", refusal.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_kernels_that_name_no_release_are_not_two_equal_ones()
    {
        using var kernel = Blob();
        var image = Image(null);

        Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(image, Followed(), kernel));

        File.WriteAllBytes(Path.Join(image, "install", "vmlinuz"), new byte[8192]);
        Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(image, Followed(), kernel));
    }

    [Theory]
    [InlineData("../install/vmlinuz")]
    [InlineData("/install/vmlinuz")]
    [InlineData("install\\vmlinuz")]
    [InlineData("")]
    public void An_image_kernel_path_that_is_not_a_plain_relative_path_is_never_opened(string path)
    {
        using var kernel = InstallerKernel("6.12.117+deb13-amd64 (x) #1\0");
        var spec = Followed();

        Assert.Throws<InstallationRefusedException>(() => DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (x) #1\0"),
            spec with { MediaIndex = spec.MediaIndex! with { ImageKernelPath = path } }, kernel));
    }

    [Fact]
    public void The_measurement_before_the_resize_refuses_a_pair_of_another_release_than_the_image()
    {
        var image = Image("6.12.117+deb13-amd64 (x) #1\0");
        using var matchingKernel = InstallerKernel("6.12.117+deb13-amd64 (x) #1\0");
        using var newerKernel = InstallerKernel("6.12.130+deb13-amd64 (x) #1\0");
        using var initrd = Blob();
        using var matching = new DirectInstallService.PinnedExternalInstallerMedia(matchingKernel, initrd);
        using var newer = new DirectInstallService.PinnedExternalInstallerMedia(newerKernel, initrd);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var sizes = DirectInstallService.MeasureMountedImage(image, Followed(), matching, logger);

        Assert.Equal((matching.Kernel.Length, matching.Initrd.Length, 0L), sizes);
        Assert.Throws<InstallationRefusedException>(() =>
            DirectInstallService.MeasureMountedImage(image, Followed(), newer, logger));
    }

    [Fact]
    public void A_measurement_is_not_reused_for_another_installer_pair_of_the_same_size()
    {
        using var kernel = InstallerKernel("6.12.117+deb13-amd64 (x) #1\0");
        using var initrd = Blob();
        using var media = new DirectInstallService.PinnedExternalInstallerMedia(kernel, initrd);
        var spec = Followed();

        var first = DirectInstallService.MeasurementInputs(spec, media);

        Assert.NotEqual(first, DirectInstallService.MeasurementInputs(spec with { KernelSha256 = new string('c', 64) }, media));
        Assert.NotEqual(first, DirectInstallService.MeasurementInputs(spec with { InitrdSha256 = new string('c', 64) }, media));
        Assert.Equal(first, DirectInstallService.MeasurementInputs(spec with { MenuTitle = "Another title" }, media));
    }

    [Fact]
    public void A_pinned_pair_is_not_compared_with_the_image()
    {
        using var kernel = InstallerKernel("6.12.107+deb13-amd64 (x) #1\0");

        DirectInstallService.RequireImageOfInstallerKernel(
            Image("6.12.117+deb13-amd64 (x) #1\0"), Followed() with { MediaIndex = null }, kernel);
    }

    [Fact]
    public async Task A_followed_pair_that_cannot_be_downloaded_is_a_refusal_that_names_the_connection()
    {
        var cache = Path.Join(_directory, "cache");

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Followed(), cache, null, CancellationToken.None,
                (_, _, _) => throw new HttpRequestException("No such host is known.")));

        Assert.Contains("Check the network connection", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task A_followed_pair_that_is_not_the_one_the_signed_list_names_is_refused_and_never_cached()
    {
        var cache = Path.Join(_directory, "cache");

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Followed(), cache, null, CancellationToken.None,
                (_, path, ct) => File.WriteAllBytesAsync(path, KernelImage("6.12.117 \0"), ct)));

        Assert.Equal("Installer files do not match the signed list", refusal.Heading);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task A_connection_lost_while_a_followed_file_is_read_is_the_connection_refusal_and_a_failed_write_is_not()
    {
        var cache = Path.Join(_directory, "cache");

        var lost = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Followed(), cache, null, CancellationToken.None,
                (_, _, _) => throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended early.")));
        Assert.Contains("Check the network connection", lost.Message, StringComparison.Ordinal);

        // The cache file is written by the same step; a full disk is not a lost connection.
        await Assert.ThrowsAsync<IOException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Followed(), cache, null, CancellationToken.None,
                (_, _, _) => throw new IOException("There is not enough space on the disk.")));
    }

    [Fact]
    public async Task Two_checks_that_fetch_the_same_new_pair_at_once_both_get_the_verified_files()
    {
        var cache = Path.Join(_directory, "cache");
        var kernel = KernelImage("6.12.117+deb13-amd64 (x) #1\0");
        var initrd = new byte[4096];
        var spec = Followed() with
        {
            KernelSha256 = Convert.ToHexString(SHA256.HashData(kernel)),
            InitrdSha256 = Convert.ToHexString(SHA256.HashData(initrd)),
        };
        Task Write(Uri url, string path, CancellationToken ct) =>
            File.WriteAllBytesAsync(path, url == spec.KernelUrl ? kernel : initrd, ct);
        DirectInstallService.PinnedExternalInstallerMedia? first = null;
        try
        {
            // The second check found no cached file; while it downloads, the first one finishes and keeps its files open.
            using var second = await DirectInstallService.AcquireExternalInstallerMediaAsync(spec, cache, null,
                CancellationToken.None, async (url, path, ct) =>
                {
                    first ??= await DirectInstallService.AcquireExternalInstallerMediaAsync(spec, cache, null, ct, Write);
                    await Write(url, path, ct);
                });

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(kernel.Length, second.Kernel.Length);
            Assert.Equal("6.12.117+deb13-amd64", second.Kernel.ReadKernelRelease());
            Assert.Equal("6.12.117+deb13-amd64", first.Kernel.ReadKernelRelease());
            Assert.Equal(2, Directory.GetFiles(cache).Length);
        }
        finally
        {
            first?.Dispose();
        }
    }

    [Fact]
    public async Task A_cancelled_download_of_a_followed_pair_stays_a_cancellation()
    {
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Followed(), Path.Join(_directory, "cache"), null, cancelled.Token,
                async (_, _, ct) =>
                {
                    await cancelled.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }));
    }
}
