using System.Collections.Immutable;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

// A projection of the existing, reobserved preparation checkpoint. It is
// deliberately read-only and carries no partition or boot execution method.
public sealed record ReviewedStagedFileInspectionV1(string RelativePath, long SizeBytes,
    string Sha256);

public sealed record ReviewedInstallerSizingV1(bool IsoOnOwnPartition,
    long PayloadMinimumBytes, long IsoMinimumBytes, long TotalShrinkBytes);

public sealed record ReviewedInstallerInspectionV1(PreparationCheckpointReference Checkpoint,
    Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    CanonicalVolumeIdentityV1 LinuxEsp, CanonicalVolumeIdentityV1 Payload,
    CanonicalVolumeIdentityV1? Iso, ReviewedInstallerFreeExtentV1 InstallerRoot,
    string StagingDirectory, ImmutableArray<ReviewedStagedFileInspectionV1> StagedFiles,
    long StagedBytes, ReviewedInstallerSizingV1 Sizing, string BootSpecSha256);

public sealed record ReviewedOwnedInstallerInspectionV1(PreparationCheckpointReference Checkpoint,
    Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    CanonicalVolumeIdentityV1 LinuxEsp, CanonicalVolumeIdentityV1 Payload,
    PreparedGptPartitionV1 LinuxRoot, InstallationOwnershipV1 Ownership,
    string StagingDirectory, ImmutableArray<ReviewedStagedFileInspectionV1> StagedFiles,
    long StagedBytes, ReviewedInstallerSizingV1 Sizing, string BootSpecSha256);

public interface IReviewedInstallerInspection
{
    ReviewedInstallerInspectionV1 Inspect(PreparationCheckpointReference checkpoint,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default);

    ReviewedOwnedInstallerInspectionV1 InspectOwned(PreparationCheckpointReference checkpoint,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class ReviewedInstallerInspection : IReviewedInstallerInspection
{
    private readonly ReviewedPartitionCreationService _reviewed;

    public ReviewedInstallerInspection(IWindowsStorageReader storage,
        PreparedLayoutStore checkpoints)
        : this(new ReviewedPartitionCreationService(storage, checkpoints)) { }

    internal ReviewedInstallerInspection(ReviewedPartitionCreationService reviewed)
        => _reviewed = reviewed;

    public ReviewedInstallerInspectionV1 Inspect(PreparationCheckpointReference checkpoint,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (requestedLinuxBytes <= 0 || pinnedIsoBytes <= 0 ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            !Path.IsPathFullyQualified(pinnedIsoPath))
            throw new InvalidDataException("The reviewed installer inspection has incomplete inputs.");

        cancellationToken.ThrowIfCancellationRequested();
        _ = _reviewed.ReopenCreatedCheckpoint(checkpoint);
        DirectInstallService.VerifySelectedIso(pinnedIsoPath, bootSpec.IsoSha256,
            cancellationToken);
        if (new FileInfo(pinnedIsoPath).Length != pinnedIsoBytes)
            throw new InvalidDataException("The selected installer image changed size.");
        var storageHandoff = _reviewed.ObserveInstallerHandoff(checkpoint, cancellationToken);
        var content = _reviewed.ObserveContentHandoff(checkpoint, storageHandoff,
            bootSpec, requestedLinuxBytes, stagingDirectory, cancellationToken);
        return new ReviewedInstallerInspectionV1(checkpoint, storageHandoff.GenerationId,
            storageHandoff.TargetDisk, storageHandoff.LinuxEsp, storageHandoff.Payload,
            storageHandoff.Iso, storageHandoff.InstallerRoot, content.StagingDirectory,
            content.StagedFiles.Select(file => new ReviewedStagedFileInspectionV1(
                file.RelativePath, file.SizeBytes, file.Sha256)).ToImmutableArray(),
            content.StagedBytes, new ReviewedInstallerSizingV1(content.Sizing.IsoOnOwnPartition,
                content.Sizing.OemDrvBytes, content.Sizing.IsoPartitionBytes,
                content.Sizing.TotalShrinkBytes), content.BootSpecSha256);
    }

    public ReviewedOwnedInstallerInspectionV1 InspectOwned(PreparationCheckpointReference checkpoint,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default) =>
        InspectOwned(checkpoint, bootSpec, requestedLinuxBytes, stagingDirectory,
            pinnedIsoPath, pinnedIsoBytes, null, cancellationToken);

    internal ReviewedOwnedInstallerInspectionV1 InspectOwned(PreparationCheckpointReference checkpoint,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        string pinnedIsoPath, long pinnedIsoBytes,
        Func<CanonicalVolumeIdentityV1, Observation<string>>? readFat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (requestedLinuxBytes <= 0 || pinnedIsoBytes <= 0 ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            !Path.IsPathFullyQualified(pinnedIsoPath))
            throw new InvalidDataException("The reviewed owned-root inspection has incomplete inputs.");
        cancellationToken.ThrowIfCancellationRequested();
        _ = _reviewed.ReopenCreatedCheckpoint(checkpoint);
        DirectInstallService.VerifySelectedIso(pinnedIsoPath, bootSpec.IsoSha256,
            cancellationToken);
        if (new FileInfo(pinnedIsoPath).Length != pinnedIsoBytes)
            throw new InvalidDataException("The selected installer image changed size.");
        var storage = _reviewed.ObserveOwnedRootBinding(checkpoint, readFat,
            cancellationToken);
        var content = _reviewed.ObserveOwnedContentHandoff(checkpoint, storage,
            bootSpec, requestedLinuxBytes, stagingDirectory, readFat,
            cancellationToken);
        return new ReviewedOwnedInstallerInspectionV1(checkpoint, storage.Storage.GenerationId,
            storage.Storage.TargetDisk, storage.Storage.LinuxEsp, storage.Storage.Payload,
            storage.Storage.LinuxRoot, storage.Ownership, content.StagingDirectory,
            content.StagedFiles.Select(file => new ReviewedStagedFileInspectionV1(
                file.RelativePath, file.SizeBytes, file.Sha256)).ToImmutableArray(),
            content.StagedBytes, new ReviewedInstallerSizingV1(content.Sizing.IsoOnOwnPartition,
                content.Sizing.OemDrvBytes, content.Sizing.IsoPartitionBytes,
                content.Sizing.TotalShrinkBytes), content.BootSpecSha256);
    }
}
