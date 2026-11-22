using System.Net;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Iso;

public sealed class InstallerMediaIndexReader(IHttpClientFactory httpFactory, ILogger<InstallerMediaIndexReader> logger)
    : IInstallerMediaIndexReader
{
    private const int MaxListBytes = 16 * 1024 * 1024;
    private const int MaxKeyBytes = 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    public async Task<InstallerBootSpec> ResolveAsync(InstallerBootSpec declared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (declared.MediaIndex is not { } index)
            return declared;
        var entries = RequireDeclaration(declared, index);

        using var client = httpFactory.CreateClient("iso");
        var signed = await FetchAsync(client, index.SignedIndexUrl, MaxListBytes, ct).ConfigureAwait(false);
        var key = await FetchAsync(client, index.SigningKeyUrl, MaxKeyBytes, ct).ConfigureAwait(false);
        if (!PgpCleartextVerifier.Verify(key, Encoding.UTF8.GetString(signed), logger, out var list,
                index.SigningKeyFingerprint))
            throw InstallationRefusedException.InstallerFilesNotAuthentic();

        // Only what the signature covers is read from here on, and only from the list of the declared release series.
        if (!list.Split('\n').Any(line => string.Equals(line.TrimEnd('\r'), index.IndexIdentityLine, StringComparison.Ordinal)))
            throw InstallationRefusedException.InstallerFilesNotAuthentic();
        var checksumFile = FindIndexEntry(list, entries.ChecksumFile)
            ?? throw InstallationRefusedException.InstallerFilesNotListed();
        var checksums = await FetchAsync(client, index.ChecksumFileUrl, MaxListBytes, ct).ConfigureAwait(false);
        if (checksums.Length != checksumFile.Size ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(checksums)), checksumFile.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw InstallationRefusedException.InstallerFilesOutOfStep();

        var names = Encoding.UTF8.GetString(checksums);
        var kernel = FindChecksum(names, entries.Kernel) ?? throw InstallationRefusedException.InstallerFilesNotListed();
        var initrd = FindChecksum(names, entries.Initrd) ?? throw InstallationRefusedException.InstallerFilesNotListed();
        logger.LogInformation("Installer boot files read from the signed index {Index}: kernel {Kernel}, initrd {Initrd}",
            index.SignedIndexUrl, kernel, initrd);
        return declared with { KernelSha256 = kernel, InitrdSha256 = initrd };
    }

    internal static (string ChecksumFile, string Kernel, string Initrd) RequireDeclaration(
        InstallerBootSpec declared, InstallerMediaIndex index)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(index);
        var fingerprint = index.SigningKeyFingerprint ?? string.Empty;
        var identity = index.IndexIdentityLine ?? string.Empty;
        // The hashes come from the index or from the provider, never from both.
        if (declared.KernelSha256 is not null || declared.InitrdSha256 is not null ||
            identity.Length == 0 || identity != identity.Trim() || identity.Any(char.IsControl) ||
            !InstallerMediaIndex.IsPlainPath(index.ImageKernelPath) ||
            fingerprint.Length is not (40 or 64) || !fingerprint.All(Uri.IsHexDigit) ||
            !SecureHttps(index.SigningKeyUrl) ||
            PathBelow(index.SignedIndexUrl, index.ChecksumFileUrl) is not { } checksumFile ||
            PathBelow(index.ChecksumFileUrl, declared.KernelUrl) is not { } kernel ||
            PathBelow(index.ChecksumFileUrl, declared.InitrdUrl) is not { } initrd ||
            string.Equals(kernel, initrd, StringComparison.Ordinal))
            throw new InvalidDataException("The provider's signed index of installer boot files is declared incompletely.");
        return (checksumFile, kernel, initrd);
    }

    // The path of a file below the folder of another, both HTTPS on one host; null when it is not below it.
    internal static string? PathBelow(Uri? folderFile, Uri? file)
    {
        if (!SecureHttps(folderFile) || !SecureHttps(file))
            return null;
        var source = folderFile!.AbsoluteUri;
        var target = file!.AbsoluteUri;
        var folder = source[..(source.LastIndexOf('/') + 1)];
        if (!target.StartsWith(folder, StringComparison.Ordinal))
            return null;
        var path = target[folder.Length..];
        return InstallerMediaIndex.IsPlainPath(path) ? path : null;
    }

    private static bool SecureHttps(Uri? url) => url is { IsAbsoluteUri: true, Query.Length: 0, Fragment.Length: 0 } &&
        string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    // An index line is "<sha256> <size> <path>"; the same path under a shorter digest is another section.
    internal static (string Sha256, long Size)? FindIndexEntry(string list, string path)
    {
        ArgumentNullException.ThrowIfNull(list);
        var found = list.Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 3 && IsSha256(parts[0]) && string.Equals(parts[2], path, StringComparison.Ordinal))
            .Select(parts => (Sha256: parts[0].ToUpperInvariant(),
                Size: long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : -1))
            .Distinct().ToArray();
        return found is [{ Size: > 0 } entry] ? entry : null;
    }

    // A checksum line is "<sha256>  <path>", the path with or without "./" in front.
    internal static string? FindChecksum(string checksums, string path)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        var found = checksums.Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && IsSha256(parts[0]) &&
                (string.Equals(parts[1], path, StringComparison.Ordinal) ||
                 string.Equals(parts[1], "./" + path, StringComparison.Ordinal)))
            .Select(parts => parts[0].ToUpperInvariant())
            .Distinct().ToArray();
        return found is [var hash] ? hash : null;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private async Task<byte[]> FetchAsync(HttpClient client, Uri url, int limit, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(FetchTimeout);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!SecureHttps(response.RequestMessage?.RequestUri))
                throw new InvalidDataException("The download was redirected away from HTTPS.");
            var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new InvalidDataException("The download is larger than a list of boot files can be.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning(ex, "The distribution no longer serves {Url}", url);
            throw InstallationRefusedException.InstallerFilesNotListed();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
        {
            logger.LogWarning(ex, "Could not fetch {Url}", url);
            throw InstallationRefusedException.InstallerFilesUnreachable(ex);
        }
    }
}
