using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Igloo.Core.Preparation;

/// <summary>Exact installed-seed binding used to retire first-boot credential inputs.</summary>
public sealed record SeedIdentityFileV1(string Path, string Sha256);

public sealed record SeedIdentityV1(int Version, Guid DiskGuid, Guid PartitionGuid,
    IReadOnlyList<SeedIdentityFileV1> Files);

/// <summary>
/// A SHA-256 the caller computed while it wrote the bytes to the seed, with the
/// length it wrote. <see cref="SeedIdentityWriter"/> accepts these so the seed is
/// read once (during the copy) instead of copied and then read back in full.
/// A known hash is used only when the file on the seed still has exactly that
/// length; anything else is hashed from the seed as before.
/// </summary>
public sealed record KnownSeedFileHash(string Sha256, long Length);

public static class SeedIdentityWriter
{
    public const string FileName = "igloo-seed-identity.json";
    // Inventory bounds exist to cap memory while reading an untrusted seed, not to
    // decide how much a user may migrate. The earlier 10,000-file cap failed a real
    // 10,218-file Documents/Downloads/Pictures/Desktop selection on 2026-10-25 -
    // after the NTFS shrink and a 4.5 GB copy. These values must match the Linux
    // verifiers (igloo-verify-seed.py, the Debian-family and Fedora agents).
    private const int MaxSelectedDataFiles = 1_000_000;
    private const int MaxSelectedDataEntries = 2_000_000;
    private const int MaxSelectedDataRoots = 1_024;
    private const int MaxSelectedPathCharacters = 256_000_000;

    private static readonly string[] SecretBearingFiles =
        ["migration-manifest.json", "ks.cfg", "preseed.cfg", "igloo-boot/initrd"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Task<SeedIdentityV1> WriteAsync(string seedRoot, Guid diskGuid,
        Guid partitionGuid, CancellationToken cancellationToken = default) =>
        WriteAsync(seedRoot, diskGuid, partitionGuid, Array.Empty<string>(), cancellationToken);

    public static Task<SeedIdentityV1> WriteAsync(string seedRoot, Guid diskGuid,
        Guid partitionGuid, IReadOnlyCollection<string> additionalRelativePaths,
        CancellationToken cancellationToken = default) =>
        WriteAsync(seedRoot, diskGuid, partitionGuid, additionalRelativePaths, null, cancellationToken);

    public static async Task<SeedIdentityV1> WriteAsync(string seedRoot, Guid diskGuid,
        Guid partitionGuid, IReadOnlyCollection<string> additionalRelativePaths,
        IReadOnlyDictionary<string, KnownSeedFileHash>? knownHashes,
        CancellationToken cancellationToken = default)
    {
        var identity = await BuildIdentityAsync(seedRoot, diskGuid, partitionGuid,
            additionalRelativePaths, knownHashes, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(seedRoot);
        var destination = Path.Join(root, FileName);
        if (File.Exists(destination) &&
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The installer seed identity file is redirected.");
        var temporary = Path.Join(root, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, identity, JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) is needed for a durable seed handoff.
                output.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporary, destination, overwrite: true);
            return identity;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Build the exact identity payload without creating or replacing a seed file.</summary>
    public static async Task<byte[]> BuildBytesAsync(string seedRoot, Guid diskGuid,
        Guid partitionGuid, IReadOnlyCollection<string> additionalRelativePaths,
        CancellationToken cancellationToken = default)
    {
        var identity = await BuildIdentityAsync(seedRoot, diskGuid, partitionGuid,
            additionalRelativePaths, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
    }

    private static async Task<SeedIdentityV1> BuildIdentityAsync(string seedRoot, Guid diskGuid,
        Guid partitionGuid, IReadOnlyCollection<string> additionalRelativePaths,
        IReadOnlyDictionary<string, KnownSeedFileHash>? knownHashes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedRoot);
        ArgumentNullException.ThrowIfNull(additionalRelativePaths);
        if (diskGuid == Guid.Empty || partitionGuid == Guid.Empty)
            throw new InvalidDataException("The installer seed has no exact GPT identity.");
        var root = Path.GetFullPath(seedRoot);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The installer seed root is unavailable or redirected.");
        var (manifestSha256, selectedMedia, selectedData) =
            await ReadSelectedPathsAsync(root, cancellationToken)
            .ConfigureAwait(false);
        var additional = additionalRelativePaths.ToArray();
        if (additional.Length > 128)
            throw new InvalidDataException("The installer seed has too many provider artifacts.");
        var seen = SecretBearingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in additional)
        {
            RequireSafeAdditionalPath(relative);
            if (!seen.Add(relative))
                throw new InvalidDataException("The installer seed has a duplicate provider artifact.");
        }
        foreach (var relative in selectedMedia)
            if (!seen.Add(relative))
                throw new InvalidDataException("The installer seed has a duplicate selected image.");
        foreach (var relative in selectedData)
            if (!seen.Add(relative))
                throw new InvalidDataException("The installer seed has a duplicate selected user file.");
        var selectedDataSet = selectedData.ToHashSet(StringComparer.Ordinal);
        var files = new List<SeedIdentityFileV1>();
        foreach (var relative in SecretBearingFiles.Concat(selectedMedia).Concat(selectedData)
                     .Concat(additional.OrderBy(path => path, StringComparer.Ordinal)))
        {
            var path = Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar));
            for (var directory = Path.GetDirectoryName(path)!;
                 !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
                 directory = Path.GetDirectoryName(directory)!)
                if ((!Directory.Exists(directory) &&
                    (additional.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                      selectedMedia.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                      selectedDataSet.Contains(relative))) ||
                    (Directory.Exists(directory) &&
                     (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("An installer identity directory is unavailable or redirected.");
            if (!File.Exists(path))
            {
                if (additional.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                    selectedMedia.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                    selectedDataSet.Contains(relative))
                    throw new InvalidDataException("A selected installer file is missing from the seed.");
                continue;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An installer identity file is redirected.");
            if (knownHashes is not null && knownHashes.TryGetValue(relative, out var known) &&
                known.Length > 0 && known.Sha256 is { Length: 64 } knownSha &&
                knownSha.All(Uri.IsHexDigit) && new FileInfo(path).Length == known.Length)
            {
                // Hashed while the bytes were written; the length still matches.
                files.Add(new SeedIdentityFileV1(relative, knownSha.ToUpperInvariant()));
                continue;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, useAsync: true);
            if (stream.Length <= 0 && !selectedDataSet.Contains(relative))
                throw new InvalidDataException("A bound installer file is empty.");
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream,
                cancellationToken).ConfigureAwait(false));
            files.Add(new SeedIdentityFileV1(relative, sha));
        }
        if (!files.Any(f => f.Path == "migration-manifest.json") ||
            !files.Any(f => f.Path is "ks.cfg" or "preseed.cfg"))
            throw new InvalidDataException("The installer seed is missing its manifest or installer config.");
        if (manifestSha256 is not null && !string.Equals(manifestSha256,
                files.Single(file => file.Path == "migration-manifest.json").Sha256,
                StringComparison.Ordinal))
            throw new IOException("The installer manifest changed while its selected images were bound.");

        return new SeedIdentityV1(1, diskGuid, partitionGuid, files);
    }

    private static async Task<(string? ManifestSha256, string[] Media, string[] Data)> ReadSelectedPathsAsync(string root,
        CancellationToken cancellationToken)
    {
        var manifest = Path.Join(root, "migration-manifest.json");
        if (!File.Exists(manifest)) return (null, [], []);
        if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The installer manifest is redirected.");
        try
        {
            var bytes = await File.ReadAllBytesAsync(manifest, cancellationToken)
                .ConfigureAwait(false);
            var manifestSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The installer manifest is not an object.");
            var paths = new List<string>(2);
            AddSelectedMedia(document.RootElement, "wallpaper", "igloo-wallpaper",
                paths);
            AddSelectedMedia(document.RootElement, "accountPicture", "igloo-avatar",
                paths);
            var data = EnumerateSelectedData(root, document.RootElement,
                cancellationToken);
            return (manifestSha256, [.. paths], data);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The installer manifest cannot bind selected media.", error);
        }
    }

    private static void AddSelectedMedia(JsonElement manifest, string property,
        string namePrefix, List<string> paths)
    {
        if (!manifest.TryGetProperty(property, out var selected) ||
            selected.ValueKind == JsonValueKind.Null)
            return;
        if (selected.ValueKind != JsonValueKind.Object ||
            !selected.TryGetProperty("fileName", out var file) ||
            file.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A selected installer image has no file name.");
        var name = file.GetString();
        var valid = namePrefix == "igloo-avatar"
            ? string.Equals(name, "igloo-avatar.jpg", StringComparison.Ordinal)
            : name is not null && Regex.IsMatch(name,
                @"\Aigloo-wallpaper\.[A-Za-z0-9]{2,8}\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!valid)
            throw new InvalidDataException("A selected installer image has an unsafe file name.");
        paths.Add(name!);
    }

    private static string[] EnumerateSelectedData(string root, JsonElement manifest,
        CancellationToken cancellationToken)
    {
        var selectedRoots = SelectedDataRoots(root, manifest);
        var observed = new List<string>();
        var observedDirectories = new List<string>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        var pathCharacters = 0;
        foreach (var namespaceName in new[] { "files", "browser-profiles" })
        {
            var namespacePath = Path.Join(root, namespaceName);
            var selectedHere = selectedRoots.Where(path => path.StartsWith(
                namespaceName + "/", StringComparison.Ordinal)).ToArray();
            if (!Directory.Exists(namespacePath))
            {
                if (selectedHere.Length > 0 || File.Exists(namespacePath))
                    throw new InvalidDataException("A selected user-data root is missing or occupied.");
                continue;
            }
            if (selectedHere.Length == 0 ||
                (File.GetAttributes(namespacePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The seed contains an unselected or redirected user-data root.");
            var pending = new Stack<string>();
            pending.Push(namespacePath);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new InvalidDataException("Selected user data contains a redirected or special entry.");
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    RequireSafeDataPath(relative);
                    if (!aliases.Add(relative.Normalize(NormalizationForm.FormC)) ||
                        ++entryCount > MaxSelectedDataEntries)
                        throw new InvalidDataException("Selected user data has an ambiguous or excessive inventory.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!selectedHere.Any(selected => RelatedDirectory(relative, selected)))
                            throw new InvalidDataException("The seed contains an unselected user-data directory.");
                        observedDirectories.Add(relative);
                        pending.Push(entry);
                        continue;
                    }
                    if (!selectedHere.Any(selected => relative.StartsWith(
                            selected + "/", StringComparison.Ordinal)))
                        throw new InvalidDataException("The seed contains an unselected user-data file.");
                    pathCharacters = checked(pathCharacters + relative.Length);
                    if (observed.Count >= MaxSelectedDataFiles ||
                        pathCharacters > MaxSelectedPathCharacters)
                        throw new InvalidDataException("The selected user-data inventory is too large.");
                    observed.Add(relative);
                }
            }
        }
        foreach (var selected in selectedRoots)
        {
            var selectedPath = Path.Join(root, selected.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(selectedPath) ||
                (File.GetAttributes(selectedPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A selected user-data directory is missing or redirected.");
        }
        var boundDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selected in selectedRoots)
            AddDirectoryPrefixes(selected, boundDirectories);
        foreach (var file in observed)
            AddDirectoryPrefixes(file[..file.LastIndexOf('/')], boundDirectories);
        foreach (var directory in observedDirectories)
            if (!boundDirectories.Contains(directory))
                throw new InvalidDataException("Selected user data contains an unbound empty directory.");
        return [.. observed.OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static void AddDirectoryPrefixes(string path, HashSet<string> directories)
    {
        while (true)
        {
            directories.Add(path);
            var slash = path.LastIndexOf('/');
            if (slash < 0) return;
            path = path[..slash];
        }
    }

    private static string[] SelectedDataRoots(string root, JsonElement manifest)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.TryGetProperty("files", out var files) && files.ValueKind != JsonValueKind.Null)
        {
            if (files.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Selected folder metadata is invalid.");
            var folderNames = new List<string>();
            if (files.TryGetProperty("folders", out var folders) &&
                folders.ValueKind != JsonValueKind.Null)
            {
                if (folders.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Selected folder metadata is invalid.");
                foreach (var folder in folders.EnumerateArray())
                {
                    if (folder.ValueKind != JsonValueKind.Object ||
                        !folder.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("A selected folder has no safe name.");
                    folderNames.Add(name.GetString()!);
                }
            }
            if (folderNames.Count == 0 &&
                files.TryGetProperty("includedFolders", out var included) &&
                included.ValueKind != JsonValueKind.Null)
            {
                if (included.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Selected folder metadata is invalid.");
                foreach (var name in included.EnumerateArray())
                {
                    if (name.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("A selected folder has no safe name.");
                    folderNames.Add(name.GetString()!);
                }
            }
            if (files.TryGetProperty("stagingPath", out var staging) &&
                staging.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Selected folder staging metadata is invalid.");
            // Bind selected folders only when the seed actually carries them. A
            // staging path in the manifest no longer implies a copy on the seed:
            // Debian-family first boot reads folders from the Windows volume.
            if (Directory.Exists(Path.Join(root, "files")))
                foreach (var name in folderNames)
                {
                    RequireSafeDataSegment(name);
                    if (!roots.Add("files/" + name) || roots.Count > MaxSelectedDataRoots)
                        throw new InvalidDataException("Selected folders have ambiguous or excessive staging roots.");
                }
        }
        if (manifest.TryGetProperty("browsers", out var browsers) &&
            browsers.ValueKind != JsonValueKind.Null)
        {
            if (browsers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Selected browser metadata is invalid.");
            foreach (var browser in browsers.EnumerateArray())
            {
                if (browser.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Selected browser metadata is invalid.");
                if (!browser.TryGetProperty("profileStagingPath", out var staged) ||
                    staged.ValueKind == JsonValueKind.Null ||
                    staged.ValueKind == JsonValueKind.String &&
                    string.IsNullOrEmpty(staged.GetString()))
                    continue; // Historical NTFS-only browser selection.
                if (staged.ValueKind != JsonValueKind.String ||
                    !browser.TryGetProperty("sourceRelativePath", out var source) ||
                    source.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Staged browser selection has no source binding.");
                var sourcePath = source.GetString()!.Replace('\\', '/');
                RequireSafeDataPath(sourcePath);
                var expected = "browser-profiles/" + sourcePath;
                if (!string.Equals(staged.GetString(), expected, StringComparison.Ordinal))
                    throw new InvalidDataException("Staged browser selection is ambiguous or changed.");
                // Bound only when the snapshot is on the seed; otherwise first boot
                // reads it from the staging directory on the Windows volume.
                if (!Directory.Exists(Path.Join(root, expected.Replace('/', Path.DirectorySeparatorChar))))
                    continue;
                if (!roots.Add(expected) || roots.Count > MaxSelectedDataRoots)
                    throw new InvalidDataException("Staged browser selection is ambiguous or changed.");
            }
        }
        return [.. roots];
    }

    private static bool RelatedDirectory(string observed, string selected) =>
        string.Equals(observed, selected, StringComparison.Ordinal) ||
        observed.StartsWith(selected + "/", StringComparison.Ordinal) ||
        selected.StartsWith(observed + "/", StringComparison.Ordinal);

    private static void RequireSafeDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Split('/').Any(segment => !SafeDataSegment(segment)))
            throw new InvalidDataException("A selected user-data path is unsafe.");
    }

    private static void RequireSafeDataSegment(string segment)
    {
        if (!SafeDataSegment(segment))
            throw new InvalidDataException("A selected user-data path is unsafe.");
    }

    private static bool SafeDataSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
            !string.Equals(segment, segment.Normalize(NormalizationForm.FormC),
                StringComparison.Ordinal) ||
            segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or ':' or '|' or '?' or '*' or '/' or '\\'))
            return false;
        var baseName = segment.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return false;
        return !(baseName.Length == 4 &&
                 (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 baseName[3] is >= '1' and <= '9');
    }

    private static void RequireSafeAdditionalPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 256 ||
            Path.IsPathRooted(relative) || relative.Contains('\\', StringComparison.Ordinal) ||
            relative.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("A provider artifact identity path is unsafe.");
        var segments = relative.Split('/');
        if (segments.Length < 2 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-')) ||
            segments[0].Equals("files", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("browser-profiles", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("EFI", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("images", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("igloo-boot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A provider artifact identity path is unsafe.");
    }
}
