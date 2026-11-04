using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    internal const string SeedReuseReceiptFileName = "igloo-seed-reuse.json";
    private sealed record SeedReuseReceipt(int Version, CanonicalVolumeIdentityV1 Target,
        IReadOnlyList<SeedIdentityFileV1> Files);

    internal static string[] SeedOutputPaths(InstallerBootSpec spec, IEnumerable<string> stagedPaths,
        bool isoOnOwnPartition)
    {
        var paths = stagedPaths.Select(path => path.Replace('\\', '/')).Concat(new[]
        {
            "igloo-boot/linux", "igloo-boot/initrd", "igloo-boot/shimx64.efi", "igloo-boot/grubx64.efi",
            "EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/grubx64.efi", SeedIdentityWriter.FileName,
        }).Concat(GrubCfgDirs.Select(path => path.Replace('\\', '/') + "/grub.cfg"))
            .Concat(spec.ExtraIsoFiles.Select(file => file.OemDrvRelativePath.Replace('\\', '/')));
        if (spec.CopyFullIsoToVolume && !isoOnOwnPartition && spec.IsoVolumeFileName is { } isoName)
            paths = paths.Append(isoName);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    // This receipt covers every file this writer may overwrite, including EFI,
    // kernel and ISO bytes not included in the first-boot retirement identity.
    internal static void WriteSeedReuseReceipt(string seedRoot, CanonicalVolumeIdentityV1 target,
        IReadOnlyCollection<string> outputPaths, CancellationToken ct) =>
        WriteSeedReuseReceipt(seedRoot, target, outputPaths, null, ct);

    // knownHashes: digests computed while the bytes were written (staging copy) or
    // already verified (the pinned ISO). Used only when the output still has exactly
    // the known length; everything else is read back and hashed.
    internal static void WriteSeedReuseReceipt(string seedRoot, CanonicalVolumeIdentityV1 target,
        IReadOnlyCollection<string> outputPaths, IReadOnlyDictionary<string, KnownSeedFileHash>? knownHashes,
        CancellationToken ct)
    {
        var files = HashSeedOutputs(seedRoot, outputPaths, ct, knownHashes);
        var receipt = new SeedReuseReceipt(1, target, files);
        var destination = SafeSeedOutputPath(seedRoot, SeedReuseReceiptFileName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt, ExistingSeedJson);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void RequireSeedReuseReceipt(string root, CanonicalVolumeIdentityV1 target,
        IReadOnlyCollection<string> outputPaths, CancellationToken ct)
    {
        var receiptPath = SafeSeedOutputPath(root, SeedReuseReceiptFileName);
        if (!File.Exists(receiptPath))
            throw new InvalidOperationException("The existing installer partition has no complete reuse receipt for its boot files. " +
                "Its contents have been preserved; the older seed identity alone does not authorize overwriting them.");
        using var stream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // The receipt lists every seed output including selected user files; its size
        // bound follows the SeedIdentityWriter inventory bounds (1,000,000 files).
        if (stream.Length is <= 0 or > 256L * 1024 * 1024)
            throw new InvalidDataException("The existing installer reuse receipt has an invalid size.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        RequireUniqueProperties(document.RootElement);
        var receipt = document.RootElement.Deserialize<SeedReuseReceipt>(ExistingSeedJson);
        if (receipt is null || receipt.Version != 1 || receipt.Target != target || receipt.Files is null ||
            receipt.Files.Count is < 1 or > 1_000_000 || receipt.Files.Any(file => file is null ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The existing reuse receipt does not bind this installer partition.");
        var owned = receipt.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = outputPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (owned.Count != receipt.Files.Count || !owned.Contains(SeedIdentityWriter.FileName) ||
            owned.Any(path => !incoming.Contains(path)))
            throw new InvalidOperationException("The existing installer partition contains output from a different preparation. " +
                "Its contents have been preserved; this run cannot safely reuse that output.");
        foreach (var path in incoming)
        {
            var fullPath = SafeSeedOutputPath(root, path);
            if ((File.Exists(fullPath) || Directory.Exists(fullPath)) && !owned.Contains(path))
                throw new InvalidOperationException("An installer destination already contains an unowned file. " +
                    "Its contents have been preserved; iGloo will not overwrite it.");
        }
        var actual = HashSeedOutputs(root, owned, ct);
        if (!receipt.Files.OrderBy(file => file.Path, StringComparer.Ordinal).SequenceEqual(actual))
            throw new InvalidDataException("The existing installer boot or payload files differ from their reuse receipt.");
    }

    private static SeedIdentityFileV1[] HashSeedOutputs(string root, IReadOnlyCollection<string> paths,
        CancellationToken ct, IReadOnlyDictionary<string, KnownSeedFileHash>? knownHashes = null)
    {
        if (paths.Count is < 1 or > 1_000_000)
            throw new InvalidDataException("The installer reuse file inventory is invalid.");
        var files = new List<SeedIdentityFileV1>(paths.Count);
        foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = SafeSeedOutputPath(root, path);
            if (knownHashes is not null && knownHashes.TryGetValue(path, out var known) &&
                known.Length > 0 && known.Sha256 is { Length: 64 } knownSha &&
                knownSha.All(Uri.IsHexDigit) && new FileInfo(fullPath).Length == known.Length)
            {
                files.Add(new(path, knownSha.ToUpperInvariant()));
                continue;
            }
            using var input = new FileStream(fullPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var digest = SHA256.HashDataAsync(input, ct).AsTask().GetAwaiter().GetResult();
            files.Add(new(path, Convert.ToHexString(digest)));
        }
        return [.. files];
    }

    private static string SafeSeedOutputPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\', StringComparison.Ordinal) ||
            relative.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.TrimEnd(' ', '.') != segment || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("The installer reuse path is unsafe.");
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.Join(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        for (var path = fullPath; !string.Equals(path, fullRoot, StringComparison.OrdinalIgnoreCase);
            path = Path.GetDirectoryName(path)!)
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installer reuse path is redirected.");
        return fullPath;
    }
}
