using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Iso;

/// <summary>
/// Downloads a distribution ISO with HTTP Range-request resumability, verifies its
/// SHA-256 hash, and verifies the GPG cleartext-signed CHECKSUM file (Fedora-style).
///
/// Partial downloads are kept as <c>&lt;filename&gt;.partial</c> alongside the final ISO.
/// On resume the service sends <c>Range: bytes=N-</c>; if the server returns 200 instead
/// of 206 (no Range support) the download restarts from zero.
/// </summary>
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

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<IsoAcquisitionResult> AcquireAsync(
        IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct = default)
    {
        var distroDir = Path.Combine(_cacheDir, spec.DistroId);
        var fileName = Path.GetFileName(spec.DownloadUrl.AbsolutePath);
        var isoPath = Path.Combine(distroDir, fileName);
        var partialPath = isoPath + ".partial";

        Directory.CreateDirectory(distroDir);

        // ── Step 0: Transport policy ──────────────────────────────────────────
        // Every artefact URL must be HTTPS. TLS is the outer layer; GPG + SHA-256
        // are the inner layers - none of them is optional when declared.
        RequireHttps(spec);

        // ── Step 1: Fetch CHECKSUM file, GPG-verify it, parse SHA-256 ────────
        // Done BEFORE downloading the ISO: a verification failure must abort the
        // acquisition before pulling gigabytes, and the authoritative hash must be
        // known up front. When distro.json ships with an empty sha256, the hash is
        // auto-resolved from the GPG-signed CHECKSUM file - no hardcoded values to
        // maintain across releases.
        //
        // FAIL-CLOSED POLICY (do not weaken):
        //  * GPG declared (signature URL + key source) → the signature MUST verify,
        //    or acquisition throws. A failed/unavailable signature never degrades
        //    into a warning.
        //  * A SHA-256 for the ISO MUST be available from the manifest or the
        //    signed checksum file, or acquisition throws - an unverifiable image
        //    is never installed.
        //  * If the manifest hash and the signed checksum hash BOTH exist they must
        //    agree, or acquisition throws (either one was tampered with).
        string? resolvedSha256 = null;
        bool gpgVerified = false;

        bool hasHardcodedHash = !string.IsNullOrWhiteSpace(spec.ExpectedSha256)
                             && !spec.ExpectedSha256.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);
        if (hasHardcodedHash)
            resolvedSha256 = spec.ExpectedSha256;

        bool gpgDeclared = spec.GpgSignatureUrl is not null
                        && (spec.GpgKeyData is { Length: > 0 } || spec.GpgKeyUrl is not null);

        if (gpgDeclared)
        {
            progress?.Report(new IsoAcquisitionProgress(
                IsoAcquisitionPhase.VerifyingGpg, 0, null, "Fetching & verifying CHECKSUM file…"));

            var (checksumContent, gpgOk) = await FetchAndVerifyChecksumAsync(spec, ct);
            if (!gpgOk)
                throw new InvalidOperationException(
                    $"GPG verification failed for {spec.DistroId}: the checksum file could not be " +
                    "authenticated against the distribution's signing key. Refusing to continue - " +
                    "this can mean a network problem or a tampered mirror. Check your connection and retry.");
            gpgVerified = true;

            var signedHash = checksumContent is not null
                ? ParseSha256FromChecksum(checksumContent, fileName)
                : null;

            if (signedHash is not null && resolvedSha256 is not null &&
                !string.Equals(signedHash, resolvedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Checksum conflict for {spec.DistroId}: the manifest pins SHA-256 " +
                    $"{resolvedSha256[..16]}… but the GPG-signed checksum file says {signedHash[..16]}…. " +
                    "One of them is wrong or tampered with. Refusing to continue.");

            if (resolvedSha256 is null && signedHash is not null)
            {
                resolvedSha256 = signedHash;
                _logger.LogInformation(
                    "SHA-256 auto-resolved from signed CHECKSUM file: {Hash}…", resolvedSha256[..16]);
            }
        }
        else if (spec.GpgSignatureUrl is not null || spec.GpgSignedDataUrl is not null)
        {
            // Signature URL without any key source: we could download the signature
            // but have nothing trusted to verify it against.
            _logger.LogWarning(
                "GPG signature declared for {DistroId} but no key source (bundled key or key URL) - " +
                "signature cannot be verified; relying on the pinned SHA-256", spec.DistroId);
        }
        else
        {
            _logger.LogWarning("No GPG signature declared for {DistroId}; relying on the pinned SHA-256", spec.DistroId);
        }

        if (resolvedSha256 is null)
            throw new InvalidOperationException(
                $"No SHA-256 hash available for {spec.DistroId} (none pinned in the manifest and none " +
                "resolvable from a signed checksum file). Refusing to download an unverifiable image.");

        // ── Step 2: Download ISO (resumable) ──────────────────────────────────
        _logger.LogInformation("Acquiring ISO for {DistroId} from {Url}", spec.DistroId, spec.DownloadUrl);
        await DownloadWithResumeAsync(spec.DownloadUrl, isoPath, partialPath, progress, ct);

        // ── Step 3: Verify SHA-256 (mandatory - resolvedSha256 is never null here) ──
        string computedHash = await ComputeSha256Async(isoPath, progress, ct);

        if (!string.Equals(computedHash, resolvedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Corrupt or tampered - delete so next run re-downloads.
            File.Delete(isoPath);
            throw new InvalidOperationException(
                $"SHA-256 mismatch for {spec.DistroId}. " +
                $"Expected {resolvedSha256[..16]}…, computed {computedHash[..16]}…");
        }
        _logger.LogInformation("SHA-256 OK: {Hash}", computedHash);

        // ── Done ─────────────────────────────────────────────────────────────
        progress?.Report(new IsoAcquisitionProgress(IsoAcquisitionPhase.Complete, 0, null, null));

        return new IsoAcquisitionResult(isoPath, Sha256Verified: true, gpgVerified, new FileInfo(isoPath).Length);
    }

    /// <summary>
    /// Rejects any non-HTTPS artefact URL. TLS alone is not the trust anchor
    /// (GPG + SHA-256 are), but allowing plain HTTP would hand an on-path attacker
    /// the checksum, signature AND key in one go.
    /// </summary>
    private static void RequireHttps(IsoSpecification spec)
    {
        foreach (var url in new[] { spec.DownloadUrl, spec.GpgSignatureUrl, spec.GpgKeyUrl, spec.GpgSignedDataUrl })
        {
            if (url is not null && !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Insecure URL rejected: {url}. All ISO, checksum, signature and key URLs must use HTTPS.");
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

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
            request, HttpCompletionOption.ResponseHeadersRead, ct);

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
            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[BufferSize];
            long downloaded = resumeFrom;
            int bytesRead;

            while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                progress?.Report(new IsoAcquisitionProgress(
                    IsoAcquisitionPhase.Downloading, downloaded, totalBytes, null));
            }

            await fileStream.FlushAsync(ct);
            downloadComplete = true;
        }
        finally
        {
            await fileStream.DisposeAsync();
        }

        if (downloadComplete)
        {
            if (File.Exists(isoPath))
                File.Delete(isoPath);
            File.Move(partialPath, isoPath);
            _logger.LogInformation("Download complete: {Path}", isoPath);
        }
    }

    // ── SHA-256 ───────────────────────────────────────────────────────────────

    private async Task<string> ComputeSha256Async(
        string filePath,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new IsoAcquisitionProgress(
            IsoAcquisitionPhase.VerifyingSha256, 0, null, "Computing SHA-256…"));

        long totalBytes = new FileInfo(filePath).Length;

        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize * 2, useAsync: true);

        var buffer = new byte[BufferSize * 2];
        long bytesProcessed = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            bytesProcessed += bytesRead;
            progress?.Report(new IsoAcquisitionProgress(
                IsoAcquisitionPhase.VerifyingSha256, bytesProcessed, totalBytes, null));
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    // ── GPG + CHECKSUM ────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the CHECKSUM file and (separately) GPG-verifies it. Failures are
    /// reported through the returned <c>verified</c> flag - the caller enforces the
    /// fail-closed policy (this method stays exception-free so logs capture the
    /// specific failure before the caller aborts).
    /// </summary>
    private async Task<(string? content, bool verified)> FetchAndVerifyChecksumAsync(
        IsoSpecification spec, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("iso");

        // Debian/Ubuntu use a detached signature (SHA256SUMS + SHA256SUMS.sign);
        // Fedora uses a single clear-signed CHECKSUM. Branch on which model the
        // distro declared so GPG is verified, never silently skipped.
        if (spec.GpgSignedDataUrl is not null)
            return await FetchAndVerifyDetachedAsync(spec, client, ct);

        // ── Step A: Download CHECKSUM file (required for SHA-256 resolution) ──
        string? checksumContent = null;
        try
        {
            _logger.LogInformation("Fetching CHECKSUM from {Url}", spec.GpgSignatureUrl);
            checksumContent = await client.GetStringAsync(spec.GpgSignatureUrl!, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CHECKSUM download failed for {DistroId}", spec.DistroId);
            return (null, false);
        }

        // ── Step B: GPG verification (result enforced fail-closed by the caller) ──
        bool gpgVerified = false;
        try
        {
            var keyBytes = await GetSigningKeyAsync(spec, client, ct);
            gpgVerified = PgpCleartextVerifier.Verify(keyBytes, checksumContent, _logger, spec.GpgKeyFingerprint);
            _logger.LogInformation("GPG verification result for {DistroId}: {Result}", spec.DistroId, gpgVerified);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPG key download/verification failed for {DistroId}", spec.DistroId);
        }

        return (checksumContent, gpgVerified);
    }

    /// <summary>
    /// Debian/Ubuntu model: a plain checksum data file (SHA256SUMS) plus a detached
    /// signature file (SHA256SUMS.sign / .gpg). Verifies the detached signature over
    /// the raw data bytes, then returns the data text for SHA-256 resolution.
    /// </summary>
    private async Task<(string? content, bool verified)> FetchAndVerifyDetachedAsync(
        IsoSpecification spec, HttpClient client, CancellationToken ct)
    {
        byte[] dataBytes;
        string dataText;
        try
        {
            _logger.LogInformation("Fetching checksum data from {Url}", spec.GpgSignedDataUrl);
            dataBytes = await client.GetByteArrayAsync(spec.GpgSignedDataUrl!, ct);
            dataText = System.Text.Encoding.UTF8.GetString(dataBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SHA256SUMS download failed for {DistroId}", spec.DistroId);
            return (null, false);
        }

        bool gpgVerified = false;
        try
        {
            _logger.LogInformation("Fetching detached signature from {Url}", spec.GpgSignatureUrl);
            var sigBytes = await client.GetByteArrayAsync(spec.GpgSignatureUrl!, ct);
            var keyBytes = await GetSigningKeyAsync(spec, client, ct);
            gpgVerified = PgpDetachedVerifier.Verify(keyBytes, dataBytes, sigBytes, _logger, spec.GpgKeyFingerprint);
            _logger.LogInformation("Detached GPG verification result for {DistroId}: {Result}", spec.DistroId, gpgVerified);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detached GPG download/verification failed for {DistroId}", spec.DistroId);
        }

        return (dataText, gpgVerified);
    }

    /// <summary>
    /// Returns the signing-key bytes: the key bundled with the distro if present,
    /// otherwise fetched from the key URL. A bundled key avoids an untrusted
    /// keyserver round-trip; either source is still subject to the fingerprint pin.
    /// </summary>
    private async Task<byte[]> GetSigningKeyAsync(IsoSpecification spec, HttpClient client, CancellationToken ct)
    {
        if (spec.GpgKeyData is { Length: > 0 } bundled)
        {
            _logger.LogInformation("Using bundled signing key for {DistroId}", spec.DistroId);
            return bundled;
        }
        _logger.LogInformation("Fetching GPG key from {Url}", spec.GpgKeyUrl);
        return await client.GetByteArrayAsync(spec.GpgKeyUrl!, ct);
    }

    /// <summary>
    /// Parses a SHA-256 hash for <paramref name="isoFileName"/> from a checksum file.
    /// Handles both layouts:
    /// <list type="bullet">
    ///   <item>Fedora "BSD" style: <c>SHA256 (filename.iso) = abcdef0123…</c></item>
    ///   <item>Debian/Ubuntu coreutils style: <c>abcdef0123…  filename.iso</c></item>
    /// </list>
    /// </summary>
    internal static string? ParseSha256FromChecksum(string checksumContent, string isoFileName)
    {
        static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

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
                        return hash.ToLowerInvariant();
                }
            }

            // Debian/Ubuntu coreutils style:  <hash>  filename.iso
            var firstToken = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsSha256(firstToken))
                return firstToken.ToLowerInvariant();
        }

        return null;
    }
}
