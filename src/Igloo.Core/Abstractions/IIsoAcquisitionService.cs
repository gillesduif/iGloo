namespace Igloo.Core.Abstractions;


public interface IIsoAcquisitionService
{
    Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default);
}


/// <summary>Everything needed to fetch one distro ISO and prove it is authentic.</summary>
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
    ReadOnlyMemory<byte>? GpgKeyData = null, string? GpgKeyFingerprint = null,
    string? IsoFilePattern = null);


public sealed record IsoAcquisitionResult(string LocalPath, bool Sha256Verified, bool GpgVerified,
    long SizeBytes, string? VerifiedSha256 = null);


public sealed record IsoAcquisitionProgress(IsoAcquisitionPhase Phase, long BytesCompleted,
    long? BytesTotal, string? Message);

public enum IsoAcquisitionPhase { ResolvingMirror, Downloading, VerifyingSha256, VerifyingGpg, Complete }

/// <summary>A classified image acquisition failure. The message and inner exception stay local;
/// clients receive only the failure kind through the command boundary.</summary>
public enum IsoAcquisitionFailureKind
{
    NetworkUnavailable,
    CacheUnavailable,
    VerificationFailed,
    SourceMetadataUnavailable,
}

public sealed class IsoAcquisitionFailureException : InvalidOperationException
{
    public IsoAcquisitionFailureException() : this(IsoAcquisitionFailureKind.SourceMetadataUnavailable,
        "ISO acquisition failed.") { }

    public IsoAcquisitionFailureException(string message)
        : this(IsoAcquisitionFailureKind.SourceMetadataUnavailable, message) { }

    public IsoAcquisitionFailureException(string message, Exception innerException)
        : this(IsoAcquisitionFailureKind.SourceMetadataUnavailable, message, innerException) { }

    public IsoAcquisitionFailureException(IsoAcquisitionFailureKind kind, string message,
        Exception? innerException = null) : base(message, innerException) => Kind = kind;

    public IsoAcquisitionFailureKind Kind { get; }
}
