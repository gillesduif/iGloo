using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    // HRESULTs of ERROR_VIRUS_INFECTED and ERROR_VIRUS_DELETED: a scanner refused the open.
    private const int VirusInfected = unchecked((int)0x800700E1);
    private const int VirusDeleted = unchecked((int)0x800700E2);

    /// <summary>Whether a failed open means the file left the partition or a scanner holds it back.</summary>
    internal static bool IsLostFile(Exception error) => error
        is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
        or IOException { HResult: VirusInfected or VirusDeleted };

    /// <summary>The files the installer partition no longer gives back, found by opening each one; a length is compared where one is given.</summary>
    internal static string[] FindLostSeedFiles(string seedRoot,
        IEnumerable<(string RelativePath, long? Length)> expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var lost = new List<string>();
        foreach (var (relative, length) in expected)
        {
            ct.ThrowIfCancellationRequested();
            var portable = relative.Replace('\\', '/');
            if (portable.Length == 0 || portable[0] == '/' || portable.Contains(':', StringComparison.Ordinal) ||
                portable.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException("The installer partition's file list names a path outside it.");
            try
            {
                // A scanner may hold the file itself; only a refusal or an absence counts.
                using var stream = new FileStream(Path.Join(seedRoot, portable.Replace('/', Path.DirectorySeparatorChar)),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                if (length is { } bytes && stream.Length != bytes)
                    lost.Add(portable);
            }
            catch (Exception error) when (IsLostFile(error))
            {
                lost.Add(portable);
            }
        }
        return [.. lost];
    }

    // Opening a file makes a scanner give its verdict; one it reports is refused here or gone, and the installer would stop on the gap.
    private void RequireCopiedFilesReadable(string seedRoot, IReadOnlyList<StagedFile> copied,
        Dictionary<string, KnownSeedFileHash> written, CancellationToken ct)
    {
        var lost = FindLostSeedFiles(seedRoot, copied.Select(file =>
        {
            var relative = file.RelativePath.Replace('\\', '/');
            return (relative, written.TryGetValue(relative, out var hash) ? hash.Length : (long?)null);
        }), ct);
        if (lost.Length == 0)
            return;
        _logger.LogError("The installer partition no longer gives back {Count} copied file(s): {Files}",
            lost.Length, string.Join(", ", lost.Take(50)));
        throw InstallationRefusedException.FilesLostFromInstallerPartition(lost);
    }

    /// <summary>Stops when a file the installer partition's identity names can no longer be opened there.</summary>
    internal static void RequireSeedHoldsItsFiles(string seedRoot, CancellationToken ct = default)
    {
        SeedIdentityV1? identity;
        try
        {
            using var stream = new FileStream(Path.Join(seedRoot, SeedIdentityWriter.FileName),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            identity = JsonSerializer.Deserialize<SeedIdentityV1>(stream, ExistingSeedJson);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The installer partition's file list cannot be read.", error);
        }
        if (identity?.Files is not { Count: > 0 } files || files.Any(file => file?.Path is null))
            throw new InvalidDataException("The installer partition's file list is empty or incomplete.");
        var lost = FindLostSeedFiles(seedRoot, files.Select(file => (file.Path, (long?)null)), ct);
        if (lost.Length > 0)
            throw InstallationRefusedException.FilesLostFromInstallerPartition(lost);
    }
}
