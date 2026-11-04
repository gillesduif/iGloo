using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

public sealed record ReviewedStagedContentV1(long TotalBytes, string InventorySha256);

// Read-only input to the Host's reviewed layout composer. This never mounts
// an ISO or acquires media; the caller supplies already acquired pinned paths.
public static class ReviewedMediaInspector
{
    public static ReviewedMediaMeasurementsV1 Inspect(string pinnedIsoPath,
        InstallerBootSpec bootSpec, string? pinnedKernelPath = null,
        string? pinnedInitrdPath = null, CancellationToken cancellationToken = default) =>
        DirectInstallService.MeasureReviewedMedia(pinnedIsoPath, bootSpec,
            pinnedKernelPath, pinnedInitrdPath, cancellationToken);

    // The reviewed Host planner and the eventual writer use the same historical
    // size policy, including staged data and the FAT32 single-file limit.
    public static ReviewedInstallerSizingV1 PlanStorageSizes(InstallerBootSpec bootSpec,
        long linuxBytes, ReviewedMediaMeasurementsV1 media, long stagedBytes)
    {
        ArgumentNullException.ThrowIfNull(media);
        var measured = DirectInstallService.PlanStorageSizes(bootSpec, linuxBytes,
            checked((long)media.FullIsoBytes), checked((long)media.KernelBytes),
            checked((long)media.InitrdBytes), checked((long)media.Stage2Bytes), stagedBytes);
        return new(measured.IsoOnOwnPartition, measured.OemDrvBytes,
            measured.IsoPartitionBytes, measured.TotalShrinkBytes);
    }

    public static long MeasureStagedBytes(string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var file in DirectInstallService.InspectStagingFiles(stagingDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + file.SizeBytes);
        }
        return total;
    }

    // Bind the complete staged tree, including same-size content changes,
    // before a reviewed native transition. Paths are sorted and length-framed
    // so two different inventories cannot share one serialized input.
    public static ReviewedStagedContentV1 InspectStagedContent(string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory))
            throw new InvalidDataException("The reviewed staging directory is not absolute.");
        var root = Path.GetFullPath(stagingDirectory);
        for (var directory = new DirectoryInfo(root); directory is not null;
             directory = directory.Parent)
            if (!directory.Exists ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The reviewed staging directory is unavailable or redirected.");

        var files = DirectInstallService.InspectStagingFiles(root)
            .Select(file => (file.SourcePath,
                RelativePath: file.RelativePath.Replace('\\', '/'), file.SizeBytes))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        if (files.Length == 0 ||
            files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != files.Length ||
            files.Any(file => !ReviewedSeedPathRules.TrySegments(
                "\\" + file.RelativePath.Replace('/', '\\'), out _)))
            throw new InvalidDataException("The reviewed staging inventory is ambiguous or unsafe.");

        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        inventory.AppendData("iGloo reviewed staged content v1\0"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(number, files.Length);
        inventory.AppendData(number[..4]);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = new FileInfo(file.SourcePath);
            if (!before.Exists ||
                (before.Attributes & FileAttributes.ReparsePoint) != 0 ||
                before.Length != file.SizeBytes)
                throw new InvalidDataException("A reviewed staged file changed before hashing.");
#pragma warning disable CA2000 // The using declaration disposes the stream on every loop exit.
            using var source = new FileStream(file.SourcePath, FileMode.Open,
                FileAccess.Read, FileShare.None, buffer.Length,
                FileOptions.SequentialScan);
#pragma warning restore CA2000
            if (source.Length != file.SizeBytes)
                throw new InvalidDataException("A reviewed staged file changed size before hashing.");
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long readBytes = 0;
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBytes = checked(readBytes + count);
                if (readBytes > file.SizeBytes)
                    throw new InvalidDataException("A reviewed staged file grew while being hashed.");
                fileHash.AppendData(buffer, 0, count);
            }
            var after = new FileInfo(file.SourcePath);
            if (readBytes != file.SizeBytes || source.Length != file.SizeBytes ||
                !after.Exists || (after.Attributes & FileAttributes.ReparsePoint) != 0 ||
                after.Length != file.SizeBytes)
                throw new InvalidDataException("A reviewed staged file changed while being hashed.");

            var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath);
            BinaryPrimitives.WriteInt32LittleEndian(number, pathBytes.Length);
            inventory.AppendData(number[..4]);
            inventory.AppendData(pathBytes);
            BinaryPrimitives.WriteInt64LittleEndian(number, file.SizeBytes);
            inventory.AppendData(number);
            inventory.AppendData(fileHash.GetHashAndReset());
            total = checked(total + file.SizeBytes);
        }
        var finalFiles = DirectInstallService.InspectStagingFiles(root)
            .Select(file => (RelativePath: file.RelativePath.Replace('\\', '/'), file.SizeBytes))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        if (!files.Select(file => (file.RelativePath, file.SizeBytes))
            .SequenceEqual(finalFiles))
            throw new InvalidDataException("The reviewed staging inventory changed while being hashed.");
        return new(total, Convert.ToHexString(inventory.GetHashAndReset()));
    }
}
