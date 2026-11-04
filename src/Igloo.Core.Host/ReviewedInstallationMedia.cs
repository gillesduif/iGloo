using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Preflight;

namespace Igloo.Core.Host;

/// <summary>First-party, pinned media inputs for a reviewed installation plan.</summary>
public sealed record ReviewedInstallationMediaV1(string DistroId, string IsoPath,
    string IsoSha256, long IsoBytes, string? KernelPath, string? InitrdPath,
    ReviewedMediaMeasurementsV1 Measurements, string ProviderRevisionSha256);

public interface IReviewedInstallationMedia
{
    Task<ReviewedInstallationMediaV1> AcquireAndInspectAsync(string distroId,
        IsoAcquisitionResult verifiedIso, CancellationToken cancellationToken = default);
}

internal sealed class ReviewedInstallationMedia(DistroRegistry registry, DistroLoader loader,
    IIsoAcquisitionService acquisition) : IReviewedInstallationMedia
{
    public async Task<ReviewedInstallationMediaV1> AcquireAndInspectAsync(string distroId,
        IsoAcquisitionResult verifiedIso, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(verifiedIso);
        if (!registry.TryGet(distroId, out var plugin))
            throw new NotSupportedException("The selected installer provider is unavailable.");
        var manifest = loader.LoadedDistros.SingleOrDefault(d => d.IsAvailable &&
            string.Equals(d.Id, distroId, StringComparison.Ordinal) &&
            d.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal));
        if (manifest is null)
            throw new NotSupportedException("The selected provider does not support direct installation.");
        var digest = verifiedIso.VerifiedSha256;
        if (!verifiedIso.Sha256Verified || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || verifiedIso.SizeBytes <= 0 ||
            !Path.IsPathFullyQualified(verifiedIso.LocalPath))
            throw new InvalidDataException("The selected installer image has no verified identity.");
        InstallerIsoProof.RequireDeclaredSignature(manifest, plugin, verifiedIso);
        if (!string.IsNullOrWhiteSpace(plugin.Metadata.IsoSha256) &&
            !string.Equals(plugin.Metadata.IsoSha256, digest,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer image differs from the provider pin.");

        using var lease = await ResourceLock.AcquireAsync("iso:" + distroId,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plugin.Metadata.IsoSha256))
        {
            // A durable artifact record can rehash its bytes, but its recorded
            // GPG bit is not a fresh signature proof. Reacquire the current
            // signed checksum and require it to identify these exact bytes.
            var fresh = await acquisition.AcquireAsync(
                IsoAcquisitionCoordinator.CreateSpecification(manifest), null,
                cancellationToken).ConfigureAwait(false);
            if (!fresh.Sha256Verified || !fresh.GpgVerified ||
                !string.Equals(fresh.VerifiedSha256, digest,
                    StringComparison.OrdinalIgnoreCase) ||
                fresh.SizeBytes != verifiedIso.SizeBytes ||
                !string.Equals(Path.GetFullPath(fresh.LocalPath),
                    Path.GetFullPath(verifiedIso.LocalPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The dynamic installer image no longer matches the signed provider checksum.");
        }
        var bootSpec = plugin.GetInstallerBootSpec() with { IsoSha256 = digest };
        string? kernelPath = null;
        string? initrdPath = null;
        if (bootSpec.KernelUrl is not null || bootSpec.InitrdUrl is not null ||
            bootSpec.KernelSha256 is not null || bootSpec.InitrdSha256 is not null)
        {
            if (bootSpec.KernelUrl is null || bootSpec.InitrdUrl is null ||
                !ValidDigest(bootSpec.KernelSha256) || !ValidDigest(bootSpec.InitrdSha256))
                throw new InvalidDataException("The external installer media pins are incomplete.");
            // Reuse the existing HTTPS, resume, cache and SHA-256 acquisition path.
            var kernel = await acquisition.AcquireAsync(new IsoSpecification(
                distroId + "-hdmedia-kernel", bootSpec.KernelUrl, bootSpec.KernelSha256!,
                null, null), null, cancellationToken).ConfigureAwait(false);
            var initrd = await acquisition.AcquireAsync(new IsoSpecification(
                distroId + "-hdmedia-initrd", bootSpec.InitrdUrl, bootSpec.InitrdSha256!,
                null, null), null, cancellationToken).ConfigureAwait(false);
            RequirePinned(kernel, bootSpec.KernelSha256!);
            RequirePinned(initrd, bootSpec.InitrdSha256!);
            kernelPath = kernel.LocalPath;
            initrdPath = initrd.LocalPath;
        }

        var measurements = ReviewedMediaInspector.Inspect(verifiedIso.LocalPath,
            bootSpec, kernelPath, initrdPath, cancellationToken);
        if (measurements.FullIsoBytes != (ulong)verifiedIso.SizeBytes)
            throw new InvalidDataException("The installer image size changed during media inspection.");
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, manifest,
            cancellationToken).ConfigureAwait(false);
        return new(distroId, verifiedIso.LocalPath, digest.ToUpperInvariant(),
            verifiedIso.SizeBytes, kernelPath, initrdPath, measurements, providerRevision);
    }

    private static bool ValidDigest(string? digest) => digest is { Length: 64 } &&
        digest.All(Uri.IsHexDigit);

    private static void RequirePinned(IsoAcquisitionResult acquired, string expected)
    {
        if (!acquired.Sha256Verified || acquired.VerifiedSha256 is null ||
            !string.Equals(acquired.VerifiedSha256, expected,
                StringComparison.OrdinalIgnoreCase) || acquired.SizeBytes <= 0 ||
            !Path.IsPathFullyQualified(acquired.LocalPath))
            throw new InvalidDataException("The external installer media failed its provider pin.");
    }
}
