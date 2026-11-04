using System.Net.Http;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    private static string ExternalInstallerMediaCache => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "installer-media-cache");

    internal static async Task<PinnedExternalInstallerMedia?> AcquireExternalInstallerMediaAsync(
        InstallerBootSpec bootSpec, string cacheDirectory,
        IProgress<DirectInstallProgress>? progress, CancellationToken ct,
        Func<Uri, string, CancellationToken, Task>? download = null)
    {
        ValidateExternalInstallerMedia(bootSpec);
        ct.ThrowIfCancellationRequested();
        if (bootSpec.KernelUrl is null) return null;
        Directory.CreateDirectory(cacheDirectory);
        RequirePlainCacheDirectory(cacheDirectory);
        download ??= (url, path, token) => DownloadExternalInstallerFileAsync(url, path, progress, token);
        var kernel = await AcquireAsync(bootSpec.KernelUrl, bootSpec.KernelSha256!).ConfigureAwait(false);
        try
        {
            var initrd = await AcquireAsync(bootSpec.InitrdUrl!, bootSpec.InitrdSha256!).ConfigureAwait(false);
            return new PinnedExternalInstallerMedia(kernel, initrd);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }

        // The caller transfers these read locks to PinnedExternalInstallerMedia;
        // acquisition failures dispose the already acquired kernel above.
#pragma warning disable CA2000
        async Task<PinnedInstallerFile> AcquireAsync(Uri url, string hash)
        {
            ct.ThrowIfCancellationRequested();
            var cached = Path.Join(cacheDirectory, hash.ToUpperInvariant() + ".bin");
            if (File.Exists(cached))
            {
                try { return new PinnedInstallerFile(cached, hash, ct); }
                catch (InvalidDataException) { /* Replace corrupt cache only after a verified download. */ }
            }
            var temporary = Path.Join(cacheDirectory, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                progress?.Report(new(DirectInstallPhase.CopyingIso, 0, 0,
                    "Downloading and verifying installer boot files…"));
                await download(url, temporary, ct).ConfigureAwait(false);
                using (var verified = new PinnedInstallerFile(temporary, hash, ct)) { }
                RequirePlainCacheDirectory(cacheDirectory);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, cached, overwrite: true);
                return new PinnedInstallerFile(cached, hash, ct);
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }
#pragma warning restore CA2000
    }

    private static void RequirePlainCacheDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installer media cache is unavailable or redirected.");
    }

    private static async Task DownloadExternalInstallerFileAsync(Uri url, string destination,
        IProgress<DirectInstallProgress>? progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUrl || !SecureHttps(finalUrl))
            throw new InvalidDataException("Installer boot media redirected away from HTTPS.");
        using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total >= Fat32MaxFileBytes)
                throw new InvalidDataException("Installer boot media exceeds the FAT32 file-size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            progress?.Report(new(DirectInstallPhase.CopyingIso, total,
                response.Content.Headers.ContentLength ?? 0, "Downloading installer boot files…"));
        }
        await output.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    internal sealed class PinnedExternalInstallerMedia(PinnedInstallerFile kernel,
        PinnedInstallerFile initrd) : IDisposable
    {
        public PinnedInstallerFile Kernel { get; } = kernel;
        public PinnedInstallerFile Initrd { get; } = initrd;
        public void Dispose()
        {
            Kernel.Dispose();
            Initrd.Dispose();
        }
    }

    internal sealed class PinnedInstallerFile : IDisposable
    {
        private readonly FileStream _source;

        internal PinnedInstallerFile(string path, string expectedHash, CancellationToken ct)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer boot media is a redirected file.");
            _source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            try
            {
                if (_source.Length is <= 0 or >= Fat32MaxFileBytes)
                    throw new InvalidDataException("Installer boot media has an invalid size.");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = _source.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    digest.AppendData(buffer, 0, count);
                }
                if (!string.Equals(Convert.ToHexString(digest.GetHashAndReset()), expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installer boot media failed its SHA-256 pin.");
                _source.Position = 0;
            }
            catch
            {
                _source.Dispose();
                throw;
            }
        }

        public long Length => _source.Length;

        internal void CopyTo(string destination, IProgress<DirectInstallProgress>? progress,
            CancellationToken ct)
        {
            _source.Position = 0;
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, 1024 * 1024);
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = _source.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
                progress?.Report(new(DirectInstallPhase.CopyingIso, _source.Position, Length));
            }
            output.Flush(flushToDisk: true);
        }

        public void Dispose() => _source.Dispose();
    }

    internal static long MeasureRequiredIsoContent(string isoRoot, InstallerBootSpec bootSpec)
    {
        long bytes = 0;
        foreach (var item in bootSpec.ExtraIsoFiles)
        {
            var path = Path.Join(isoRoot, item.IsoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                if (item.Required)
                    throw new FileNotFoundException("A required installer image file is missing.", path);
                continue;
            }
            var length = new FileInfo(path).Length;
            if (length <= 0 || length >= Fat32MaxFileBytes)
                throw new InvalidDataException("An installer image payload has an invalid size.");
            bytes = checked(bytes + length);
        }
        return bytes;
    }
}
