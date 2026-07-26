using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Iso;

public sealed class IsoAcquisitionService : IIsoAcquisitionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IsoAcquisitionService> _logger;
    private readonly string _cacheDir;

    private const int BufferSize = 81_920; // 80 KB

    public IsoAcquisitionService(
        IHttpClientFactory httpFactory,
        ILogger<IsoAcquisitionService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache");
    }

    //   Public API                               

    public async Task<IsoAcquisitionResult> AcquireAsync(
        IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        //   Step 0: Transport policy
        // Every artefact URL must be HTTPS. TLS is the outer layer; GPG + SHA-256
        // are the inner layers - none of them is optional when declared.
        RequireHttps(spec);

        //   Step 1: Fetch + (when declared) GPG-verify the checksum file. It authenticates the
        // download, carries the authoritative SHA-256, and - for distros that rotate their ISO
        // filename each point release - is where the current filename is read from.
        var (checksumContent, gpgVerified) = await ResolveChecksumAsync(spec, progress, ct).ConfigureAwait(false);

        //   Step 2: Resolve the download URL + filename (pattern-aware).
        var (downloadUrl, fileName) = ResolveDownloadTarget(spec, checksumContent, gpgVerified);

        var distroDir = Path.Combine(_cacheDir, spec.DistroId);
        var isoPath = Path.Combine(distroDir, fileName);
        var partialPath = isoPath + ".partial";
        Directory.CreateDirectory(distroDir);

        //   Step 3: Resolve the authoritative SHA-256 for that filename.
        var resolvedSha256 = ResolveTrustedSha256(spec, checksumContent, gpgVerified, fileName);

        //   Step 4: Download ISO (resumable)
        _logger.LogInformation("Acquiring ISO for {DistroId} from {Url}", spec.DistroId, downloadUrl);
        await DownloadWithResumeAsync(downloadUrl, isoPath, partialPath, progress, ct).ConfigureAwait(false);

        //   Step 5: Verify SHA-256 (mandatory - resolvedSha256 is never null here)
        string computedHash = await ComputeSha256Async(isoPath, progress, ct).ConfigureAwait(false);

        if (!string.Equals(computedHash, resolvedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Corrupt or tampered - delete so next run re-downloads.
            File.Delete(isoPath);
            throw new InvalidOperationException(
                $"SHA-256 mismatch for {spec.DistroId}. " +
                $"Expected {resolvedSha256[..16]}…, computed {computedHash[..16]}…");
        }
        _logger.LogInformation("SHA-256 OK: {Hash}", computedHash);

        //   Done                               ─
        progress?.Report(new IsoAcquisitionProgress(IsoAcquisitionPhase.Complete, 0, null, null));

        return new IsoAcquisitionResult(isoPath, Sha256Verified: true, gpgVerified, new FileInfo(isoPath).Length);
    }

    /// <summary>
    /// Fetches the checksum file and, when GPG is declared, verifies it (fail-closed: a declared
    /// signature that does not verify aborts the acquisition). Returns the checksum text - the source
    /// for both the current ISO filename and its SHA-256 - or null when no checksum is declared.
    /// </summary>
    private async Task<(string? Content, bool GpgVerified)> ResolveChecksumAsync(
        IsoSpecification spec, IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct)
    {
        bool gpgDeclared = spec.GpgSignatureUrl is not null
                        && (spec.GpgKeyData is { Length: > 0 } || spec.GpgKeyUrl is not null);

        if (!gpgDeclared)
        {
            if (spec.GpgSignatureUrl is not null || spec.GpgSignedDataUrl is not null)
                _logger.LogWarning(
                    "GPG signature declared for {DistroId} but no key source (bundled key or key URL) - " +
                    "signature cannot be verified; relying on the pinned SHA-256", spec.DistroId);
            else
                _logger.LogWarning("No GPG signature declared for {DistroId}; relying on the pinned SHA-256", spec.DistroId);
            return (null, false);
        }

        progress?.Report(new IsoAcquisitionProgress(
            IsoAcquisitionPhase.VerifyingGpg, 0, null, "Fetching & verifying CHECKSUM file…"));

        var (checksumContent, gpgOk) = await FetchAndVerifyChecksumAsync(spec, ct).ConfigureAwait(false);
        if (!gpgOk)
            throw new InvalidOperationException(
                $"GPG verification failed for {spec.DistroId}: the checksum file could not be authenticated " +
                "against the distribution's signing key. Refusing to continue - this can mean a network problem " +
                "or a tampered mirror. Check your connection and retry.");

        return (checksumContent, true);
    }

    /// <summary>
    /// Resolves the ISO download URL and filename. With an <see cref="IsoSpecification.IsoFilePattern"/>
    /// the filename is taken from the GPG-verified checksum and the URL rebuilt against the manifest
    /// URL's directory, so a new point release needs no manifest edit; otherwise the manifest URL is
    /// used verbatim. Resolving from the checksum requires that checksum to be GPG-verified.
    /// </summary>
    private (Uri Url, string FileName) ResolveDownloadTarget(
        IsoSpecification spec, string? checksumContent, bool gpgVerified)
    {
        if (string.IsNullOrWhiteSpace(spec.IsoFilePattern))
            return (spec.DownloadUrl, Path.GetFileName(spec.DownloadUrl.AbsolutePath));

        if (!gpgVerified || checksumContent is null)
            throw new InvalidOperationException(
                $"{spec.DistroId} resolves its ISO filename from the signed checksum file, but that file was " +
                "not available and GPG-verified. Refusing to guess a download URL.");

        var fileName = ResolveIsoFileName(checksumContent, spec.IsoFilePattern)
            ?? throw new InvalidOperationException(
                $"No ISO matching '{spec.IsoFilePattern}' was found in {spec.DistroId}'s checksum file.");

        var url = new Uri(spec.DownloadUrl, fileName);
        _logger.LogInformation("Resolved current ISO for {DistroId}: {File}", spec.DistroId, fileName);
        return (url, fileName);
    }

    /// <summary>
    /// Returns the first filename in a checksum file matching <paramref name="pattern"/> (a regex),
    /// handling both coreutils ("&lt;hash&gt;  file.iso") and BSD ("SHA256 (file.iso) = &lt;hash&gt;")
    /// layouts. The pattern should anchor the full filename so it never matches a hash token.
    /// </summary>
    internal static string? ResolveIsoFileName(string checksumContent, string pattern)
    {
        var regex = new Regex(pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

        foreach (var line in checksumContent.Split('\n'))
        {
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = token.Trim('(', ')');
                if (regex.IsMatch(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the authoritative SHA-256 for <paramref name="fileName"/>: the manifest's pinned hash,
    /// the hash from the GPG-verified checksum file, or both (which must then agree). Throws when none
    /// is available - an unverifiable image is never downloaded.
    /// </summary>
    private string ResolveTrustedSha256(
        IsoSpecification spec, string? checksumContent, bool gpgVerified, string fileName)
    {
        bool hasHardcodedHash = !string.IsNullOrWhiteSpace(spec.ExpectedSha256)
                             && !spec.ExpectedSha256.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);
        string? resolved = hasHardcodedHash ? spec.ExpectedSha256 : null;

        var signedHash = gpgVerified && checksumContent is not null
            ? ParseSha256FromChecksum(checksumContent, fileName)
            : null;

        if (signedHash is not null && resolved is not null &&
            !string.Equals(signedHash, resolved, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum conflict for {spec.DistroId}: the manifest pins SHA-256 {resolved[..16]}… but the " +
                $"GPG-signed checksum file says {signedHash[..16]}…. One of them is wrong or tampered with. " +
                "Refusing to continue.");

        if (resolved is null && signedHash is not null)
        {
            resolved = signedHash;
            _logger.LogInformation("SHA-256 auto-resolved from signed CHECKSUM file: {Hash}…", resolved[..16]);
        }

        if (resolved is null)
            throw new InvalidOperationException(
                $"No SHA-256 hash available for {spec.DistroId} (none pinned in the manifest and none resolvable " +
                "from a signed checksum file). Refusing to download an unverifiable image.");

        return resolved;
    }

    private static void RequireHttps(IsoSpecification spec)
    {
        foreach (var url in new[] { spec.DownloadUrl, spec.GpgSignatureUrl, spec.GpgKeyUrl, spec.GpgSignedDataUrl })
        {
            if (url is not null && !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Insecure URL rejected: {url}. All ISO, checksum, signature and key URLs must use HTTPS.");
        }
    }

    //   Download                                

    private async Task DownloadWithResumeAsync(
        Uri url,
        string isoPath,
        string partialPath,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct)
    {
        if (File.Exists(isoPath))
        {
            _logger.LogInformation("ISO already cached at {Path}, skipping download", isoPath);
            return;
        }

        long resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (resumeFrom > 0)
            _logger.LogInformation("Resuming download from byte {Offset:N0}", resumeFrom);

        using var client = _httpFactory.CreateClient("iso");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Server ignored our Range header → restart
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _logger.LogWarning("Server does not support Range; restarting download from the beginning");
            resumeFrom = 0;
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }

        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long? totalBytes = contentLength.HasValue ? contentLength.Value + resumeFrom : null;

        var fileMode = resumeFrom > 0 ? FileMode.Append : FileMode.Create;
        var fileStream = new FileStream(
            partialPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        bool downloadComplete = false;
        try
        {
            var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var responseStreamCfg = responseStream.ConfigureAwait(false);
            var buffer = new byte[BufferSize];
            long downloaded = resumeFrom;
            int bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                downloaded += bytesRead;
                progress?.Report(new IsoAcquisitionProgress(
                    IsoAcquisitionPhase.Downloading, downloaded, totalBytes, null));
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            downloadComplete = true;
        }
        finally
        {
            await fileStream.DisposeAsync().ConfigureAwait(false);
        }

        if (downloadComplete)
        {
            if (File.Exists(isoPath))
                File.Delete(isoPath);
            File.Move(partialPath, isoPath);
            _logger.LogInformation("Download complete: {Path}", isoPath);
        }
    }

    //   SHA-256                                ─

    private static async Task<string> ComputeSha256Async(
        string filePath,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new IsoAcquisitionProgress(
            IsoAcquisitionPhase.VerifyingSha256, 0, null, "Computing SHA-256…"));

        long totalBytes = new FileInfo(filePath).Length;

        using var sha256 = SHA256.Create();
        var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize * 2, useAsync: true);
        await using var streamCfg = stream.ConfigureAwait(false);

        var buffer = new byte[BufferSize * 2];
        long bytesProcessed = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            bytesProcessed += bytesRead;
            progress?.Report(new IsoAcquisitionProgress(
                IsoAcquisitionPhase.VerifyingSha256, bytesProcessed, totalBytes, null));
        }

        sha256.TransformFinalBlock([], 0, 0);
        return ToLowerHex(sha256.Hash!);
    }

    
    private static string ToLowerHex(byte[] bytes) =>
        string.Create(bytes.Length * 2, bytes, static (chars, src) =>
        {
            const string hex = "0123456789abcdef";
            for (var i = 0; i < src.Length; i++)
            {
                chars[i * 2] = hex[src[i] >> 4];
                chars[i * 2 + 1] = hex[src[i] & 0xF];
            }
        });

    //   GPG + CHECKSUM                             

    private async Task<(string? content, bool verified)> FetchAndVerifyChecksumAsync(
        IsoSpecification spec, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("iso");

        // Debian/Ubuntu use a detached signature (SHA256SUMS + SHA256SUMS.sign);
        // Fedora uses a single clear-signed CHECKSUM. Branch on which model the
        // distro declared so GPG is verified, never silently skipped.
        if (spec.GpgSignedDataUrl is not null)
            return await FetchAndVerifyDetachedAsync(spec, client, ct).ConfigureAwait(false);

        //   Step A: Download CHECKSUM file (required for SHA-256 resolution)
        string? checksumContent = null;
        try
        {
            _logger.LogInformation("Fetching CHECKSUM from {Url}", spec.GpgSignatureUrl);
            checksumContent = await client.GetStringAsync(spec.GpgSignatureUrl!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "CHECKSUM download failed for {DistroId}", spec.DistroId);
            return (null, false);
        }

        //   Step B: GPG verification (result enforced fail-closed by the caller)
        // Verify itself never throws; only the key fetch can, so this catch is HTTP-scoped.
        bool gpgVerified = false;
        try
        {
            var keyBytes = await GetSigningKeyAsync(spec, client, ct).ConfigureAwait(false);
            gpgVerified = PgpCleartextVerifier.Verify(keyBytes, checksumContent, _logger, spec.GpgKeyFingerprint);
            _logger.LogInformation("GPG verification result for {DistroId}: {Result}", spec.DistroId, gpgVerified);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "GPG key download/verification failed for {DistroId}", spec.DistroId);
        }

        return (checksumContent, gpgVerified);
    }

    private async Task<(string? content, bool verified)> FetchAndVerifyDetachedAsync(
        IsoSpecification spec, HttpClient client, CancellationToken ct)
    {
        byte[] dataBytes;
        string dataText;
        try
        {
            _logger.LogInformation("Fetching checksum data from {Url}", spec.GpgSignedDataUrl);
            dataBytes = await client.GetByteArrayAsync(spec.GpgSignedDataUrl!, ct).ConfigureAwait(false);
            dataText = Encoding.UTF8.GetString(dataBytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "SHA256SUMS download failed for {DistroId}", spec.DistroId);
            return (null, false);
        }

        // Verify itself never throws; only the downloads can, so this catch is HTTP-scoped.
        bool gpgVerified = false;
        try
        {
            _logger.LogInformation("Fetching detached signature from {Url}", spec.GpgSignatureUrl);
            var sigBytes = await client.GetByteArrayAsync(spec.GpgSignatureUrl!, ct).ConfigureAwait(false);
            var keyBytes = await GetSigningKeyAsync(spec, client, ct).ConfigureAwait(false);
            gpgVerified = PgpDetachedVerifier.Verify(keyBytes, dataBytes, sigBytes, _logger, spec.GpgKeyFingerprint);
            _logger.LogInformation("Detached GPG verification result for {DistroId}: {Result}", spec.DistroId, gpgVerified);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Detached GPG download/verification failed for {DistroId}", spec.DistroId);
        }

        return (dataText, gpgVerified);
    }

    private async Task<byte[]> GetSigningKeyAsync(IsoSpecification spec, HttpClient client, CancellationToken ct)
    {
        if (spec.GpgKeyData is { Length: > 0 } bundled)
        {
            _logger.LogInformation("Using bundled signing key for {DistroId}", spec.DistroId);
            return bundled.ToArray();
        }
        _logger.LogInformation("Fetching GPG key from {Url}", spec.GpgKeyUrl);
        return await client.GetByteArrayAsync(spec.GpgKeyUrl!, ct).ConfigureAwait(false);
    }

    internal static string? ParseSha256FromChecksum(string checksumContent, string isoFileName)
    {
        static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

        // Normalise to the lowercase hex that ComputeSha256Async produces, so the two
        // sides compare equal char-for-char even before the OrdinalIgnoreCase check.
        static string ToLower(string hex) => new(hex.Select(char.ToLowerInvariant).ToArray());

        foreach (var line in checksumContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                !trimmed.Contains(isoFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Fedora "BSD" style:  SHA256 (filename.iso) = <hash>
            if (trimmed.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase))
            {
                var eqIdx = trimmed.LastIndexOf('=');
                if (eqIdx >= 0)
                {
                    var hash = trimmed[(eqIdx + 1)..].Trim();
                    if (IsSha256(hash))
                        return ToLower(hash);
                }
            }

            // Debian/Ubuntu coreutils style:  <hash>  filename.iso
            var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsSha256(firstToken))
                return ToLower(firstToken);
        }

        return null;
    }
}
