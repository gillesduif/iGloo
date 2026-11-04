using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallExternalMediaTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "igloo-external-media-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Kernel = Encoding.UTF8.GetBytes("pinned hd-media kernel");
    private static readonly byte[] Initrd = Encoding.UTF8.GetBytes("pinned hd-media initrd with a different size");

    public DirectInstallExternalMediaTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task AcquiredPairIsMeasuredAndCopiedFromLockedVerifiedFiles()
    {
        var calls = 0;
        var spec = Specification();
        using (var acquired = await DirectInstallService.AcquireExternalInstallerMediaAsync(
                   spec, _directory, null, CancellationToken.None, Download))
        {
            Assert.NotNull(acquired);
            Assert.Equal(Kernel.Length, acquired.Kernel.Length);
            Assert.Equal(Initrd.Length, acquired.Initrd.Length);
            var cacheFile = Path.Join(_directory, spec.KernelSha256!.ToUpperInvariant() + ".bin");
            Assert.Throws<IOException>(() => File.WriteAllBytes(cacheFile, [0]));
            var copied = Path.Join(_directory, "copied-initrd");
            acquired.Initrd.CopyTo(copied, null, CancellationToken.None);
            Assert.Equal(Initrd, await File.ReadAllBytesAsync(copied));
        }
        using var cached = await DirectInstallService.AcquireExternalInstallerMediaAsync(
            spec, _directory, null, CancellationToken.None, (_, _, _) =>
                throw new InvalidOperationException("Cached verified media must work offline."));
        Assert.NotNull(cached);
        Assert.Equal(2, calls);

        Task Download(Uri url, string path, CancellationToken ct)
        {
            calls++;
            return File.WriteAllBytesAsync(path, url == spec.KernelUrl ? Kernel : Initrd, ct);
        }
    }

    [Fact]
    public async Task ASecondDownloadFailureLeavesNoPartialPairAndReleasesTheKernel()
    {
        var spec = Specification();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(spec, _directory, null,
                CancellationToken.None, (url, path, ct) => url == spec.KernelUrl
                    ? File.WriteAllBytesAsync(path, Kernel, ct)
                    : throw new HttpRequestException("Offline")));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
        var cachedKernel = Path.Join(_directory, spec.KernelSha256!.ToUpperInvariant() + ".bin");
        using var writable = File.Open(cachedKernel, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(Kernel.Length, writable.Length);
    }

    [Fact]
    public async Task HashMismatchNeverBecomesACachedBootFile()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DirectInstallService.AcquireExternalInstallerMediaAsync(Specification(), _directory,
                null, CancellationToken.None, (_, path, ct) => File.WriteAllBytesAsync(path, [99], ct)));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CorruptCacheIsReplacedOnlyByCorrectPinnedBytes()
    {
        var spec = Specification();
        var cachedKernel = Path.Join(_directory, spec.KernelSha256!.ToUpperInvariant() + ".bin");
        await File.WriteAllBytesAsync(cachedKernel, [9, 9]);
        using var acquired = await DirectInstallService.AcquireExternalInstallerMediaAsync(spec,
            _directory, null, CancellationToken.None, (url, path, ct) =>
                File.WriteAllBytesAsync(path, url == spec.KernelUrl ? Kernel : Initrd, ct));
        Assert.NotNull(acquired);
        Assert.Equal(Kernel, await File.ReadAllBytesAsync(cachedKernel));
    }

    [Fact]
    public void MissingRequiredIsoPayloadFailsDuringMeasurement()
    {
        var spec = Specification() with
        {
            ExtraIsoFiles = [new IsoFileStage("images/install.img", "images/install.img", true)],
        };
        Assert.Throws<FileNotFoundException>(() =>
            DirectInstallService.MeasureRequiredIsoContent(_directory, spec));
        Directory.CreateDirectory(Path.Join(_directory, "images"));
        File.WriteAllBytes(Path.Join(_directory, "images", "install.img"), [1, 2, 3]);
        Assert.Equal(3, DirectInstallService.MeasureRequiredIsoContent(_directory, spec));
    }

    private static InstallerBootSpec Specification() => new()
    {
        MenuTitle = "fixture", KernelCmdline = "fixture",
        KernelUrl = new Uri("https://example.invalid/kernel"),
        InitrdUrl = new Uri("https://example.invalid/initrd"),
        KernelSha256 = Convert.ToHexString(SHA256.HashData(Kernel)),
        InitrdSha256 = Convert.ToHexString(SHA256.HashData(Initrd)),
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
