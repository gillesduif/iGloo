namespace Igloo.Core.Abstractions;

/// <summary>Downloads a distro ISO (resumable) and verifies it: SHA-256 plus GPG when declared.</summary>
public interface IIsoAcquisitionService
{
    Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default);
}

/// <summary>Everything needed to download and verify one distro's ISO.</summary>
/// <param name="GpgKeyData">
/// The trusted signing key bundled with the distro (preferred over fetching
/// <paramref name="GpgKeyUrl"/>: no untrusted keyserver round-trip).
/// </param>
/// <param name="GpgKeyFingerprint">
/// The pinned 160-bit fingerprint; the signing key MUST match it.
/// </param>
/// <param name="GpgSignedDataUrl">
/// Set for the detached-signature model (Debian/Ubuntu): the plain checksum
/// data file (SHA256SUMS) that <paramref name="GpgSignatureUrl"/> detach-signs.
/// When null, <paramref name="GpgSignatureUrl"/> is treated as a Fedora-style
/// clear-signed CHECKSUM.
/// </param>
public sealed record IsoSpecification(string DistroId, Uri DownloadUrl, string ExpectedSha256,
    Uri? GpgSignatureUrl, Uri? GpgKeyUrl, Uri? GpgSignedDataUrl = null,
    byte[]? GpgKeyData = null, string? GpgKeyFingerprint = null);

/// <summary>Outcome of a completed acquisition; verification flags are informational only (failure throws).</summary>
public sealed record IsoAcquisitionResult(string LocalPath, bool Sha256Verified, bool GpgVerified, long SizeBytes);

/// <summary>Progress snapshot reported during ISO acquisition.</summary>
public sealed record IsoAcquisitionProgress(IsoAcquisitionPhase Phase, long BytesCompleted,
    long? BytesTotal, string? Message);

public enum IsoAcquisitionPhase { ResolvingMirror, Downloading, VerifyingSha256, VerifyingGpg, Complete }
