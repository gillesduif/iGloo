namespace Igloo.Core.Abstractions;


public interface IIsoAcquisitionService
{
    Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default);
}


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
    ReadOnlyMemory<byte>? GpgKeyData = null, string? GpgKeyFingerprint = null);


public sealed record IsoAcquisitionResult(string LocalPath, bool Sha256Verified, bool GpgVerified, long SizeBytes);


public sealed record IsoAcquisitionProgress(IsoAcquisitionPhase Phase, long BytesCompleted,
    long? BytesTotal, string? Message);

public enum IsoAcquisitionPhase { ResolvingMirror, Downloading, VerifyingSha256, VerifyingGpg, Complete }
