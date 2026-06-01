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
    private readonly IHttpClientFactory         _httpFactory;
    private readonly ILogger<IsoAcquisitionService> _logger;
    private readonly string                     _cacheDir;

    private const int BufferSize = 81_920; // 80 KB

    public IsoAcquisitionService(
        IHttpClientFactory httpFactory,
        ILogger<IsoAcquisitionService> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
        _cacheDir    = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<IsoAcquisitionResult> AcquireAsync(
        IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress,
        CancellationToken ct = default)
    {
        var distroDir   = Path.Combine(_cacheDir, spec.DistroId);
        var fileName    = Path.GetFileName(spec.DownloadUrl.AbsolutePath);
        var isoPath     = Path.Combine(distroDir, fileName);
        var partialPath = isoPath + ".partial";

        Directory.CreateDirectory(distroDir);

        // ── Step 1: Fetch CHECKSUM file, GPG-verify it, parse SHA-256 ────────
        // We do this BEFORE downloading the ISO so we have the authoritative hash
        // ready.  When distro.json ships with an empty sha256, the hash is
        // auto-resolved from the signed CHECKSUM file — no hardcoded values to
        // maintain across Fedora releases.
        string? resolvedSha256 = null;
        bool    gpgVerified    = false;

        bool hasHardcodedHash = !string.IsNullOrWhiteSpace(spec.ExpectedSha256)
                             && !spec.ExpectedSha256.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase);
        if (hasHardcodedHash)
            resolvedSha256 = spec.ExpectedSha256;

        if (spec.GpgSignatureUrl is not null && spec.GpgKeyUrl is not null)
        {
            progress?.Report(new IsoAcquisitionProgress(
                IsoAcquisitionPhase.VerifyingGpg, 0, null, "Fetching & verifying CHECKSUM file…"));

            var (checksumContent, gpgOk) = await FetchAndVerifyChecksumAsync(spec, ct);
            gpgVerified = gpgOk;

            // Auto-resolve SHA-256 from the signed CHECKSUM file when no
            // hardcoded hash is present in the plugin manifest.
            if (resolvedSha256 is null && checksumContent is not null)
            {
                resolvedSha256 = ParseSha256FromChecksum(checksumContent, fileName);
                if (resolvedSha256 is not null)
                    _logger.LogInformation(
                        "SHA-256 auto-resolved from CHECKSUM file: {Hash}…", resolvedSha256[..16]);
                else
                    _logger.LogWarning(
                        "SHA-256 for {File} not found in CHECKSUM file", fileName);
            }
        }
        else
        {
            _logger.LogWarning("GPG check skipped: no signature URL for {DistroId}", spec.DistroId);
        }

        // ── Step 2: Download ISO (resumable) ──────────────────────────────────
        _logger.LogInformation("Acquiring ISO for {DistroId} from {Url}", spec.DistroId, spec.DownloadUrl);
        await DownloadWithResumeAsync(spec.DownloadUrl, isoPath, partialPath, progress, ct);

        // ── Step 3: Verify SHA-256 ────────────────────────────────────────────
        bool   sha256Verified = false;
        string computedHash   = await ComputeSha256Async(isoPath, progress, ct);

        if (resolvedSha256 is not null)
        {
            sha256Verified = string.Equals(computedHash, resolvedSha256, StringComparison.OrdinalIgnoreCase);
            if (!sha256Verified)
            {
                // Corrupt or tampered — delete so next run re-downloads.
                File.Delete(isoPath);
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {spec.DistroId}. " +
                    $"Expected {resolvedSha256[..16]}…, computed {computedHash[..16]}…");
            }
            _logger.LogInformation("SHA-256 OK: {Hash}", computedHash);
        }
        else
        {
            _logger.LogWarning(
                "SHA-256 check skipped: no expected hash available for {DistroId}", spec.DistroId);
        }

        // ── Done ─────────────────────────────────────────────────────────────
        progress?.Report(new IsoAcquisitionProgress(IsoAcquisitionPhase.Complete, 0, null, null));

        return new IsoAcquisitionResult(isoPath, sha256Verified, gpgVerified, new FileInfo(isoPath).Length);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async Task DownloadWithResumeAsync(
        Uri    url,
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

        using var client  = _httpFactory.CreateClient("iso");
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
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }

        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long? totalBytes    = contentLength.HasValue ? contentLength.Value + resumeFrom : null;

        var fileMode = resumeFrom > 0 ? FileMode.Append : FileMode.Create;
        var fileStream = new FileStream(
            partialPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        bool downloadComplete = false;
        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var buffer     = new byte[BufferSize];
            long downloaded = resumeFrom;
            int  bytesRead;

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
            if (File.Exists(isoPath)) File.Delete(isoPath);
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

        var  buffer         = new byte[BufferSize * 2];
        long bytesProcessed = 0;
        int  bytesRead;

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
    /// Downloads the CHECKSUM file and (separately) GPG-verifies it.
    /// The two steps are independent: a GPG failure never suppresses the
    /// CHECKSUM content, so SHA-256 can still be resolved even when GPG
    /// verification fails or the key ring cannot be parsed.
    /// </summary>
    private async Task<(string? content, bool verified)> FetchAndVerifyChecksumAsync(
        IsoSpecification spec, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("iso");

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

        // ── Step B: GPG verification (best-effort; never blocks SHA-256 use) ──
        bool gpgVerified = false;
        try
        {
            _logger.LogInformation("Fetching GPG key from {Url}", spec.GpgKeyUrl);
            var keyBytes = await client.GetByteArrayAsync(spec.GpgKeyUrl!, ct);
            gpgVerified = PgpCleartextVerifier.Verify(keyBytes, checksumContent, _logger);
            _logger.LogInformation("GPG verification result for {DistroId}: {Result}", spec.DistroId, gpgVerified);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GPG key download/verification failed for {DistroId} — SHA-256 will still be checked",
                spec.DistroId);
        }

        return (checksumContent, gpgVerified);
    }

    /// <summary>
    /// Parses a SHA-256 hash for <paramref name="isoFileName"/> from a Fedora-style
    /// GPG cleartext-signed CHECKSUM file.
    /// Expected line format: <c>SHA256 (filename.iso) = abcdef0123…</c>
    /// </summary>
    private static string? ParseSha256FromChecksum(string checksumContent, string isoFileName)
    {
        foreach (var line in checksumContent.Split('\n'))
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase)) continue;
            if (!trimmed.Contains(isoFileName, StringComparison.OrdinalIgnoreCase)) continue;

            var eqIdx = trimmed.LastIndexOf('=');
            if (eqIdx < 0) continue;

            var hash = trimmed[(eqIdx + 1)..].Trim();
            if (hash.Length == 64 && hash.All(c => Uri.IsHexDigit(c)))
                return hash.ToLowerInvariant();
        }

        return null;
    }
}
