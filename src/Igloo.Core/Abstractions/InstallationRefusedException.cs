namespace Igloo.Core.Abstractions;

/// <summary>A refusal whose message tells the user what to do next; raised before Windows is resized wherever the cause can be known by then.</summary>
public sealed class InstallationRefusedException : InvalidOperationException
{
    public InstallationRefusedException() { }

    public InstallationRefusedException(string message) : base(message) { }

    public InstallationRefusedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The refusal for a selected file that cannot go onto a FAT32 installer partition, naming the file.</summary>
    public static InstallationRefusedException FileTooLargeForInstallerPartition(string path, long bytes) =>
        new(FormattableString.Invariant($"'{path}' is {bytes / (1024.0 * 1024 * 1024):N1} GiB. ") +
            "This distribution carries your files on an installer partition that cannot hold a file of 4 GiB or more. " +
            "Go back to Configuration and leave its folder out, or move the file elsewhere, then continue.");

    /// <summary>The refusal for copied files that the installer partition no longer gives back, naming them by their path on it.</summary>
    public static InstallationRefusedException FilesLostFromInstallerPartition(IReadOnlyList<string> seedPaths)
    {
        ArgumentNullException.ThrowIfNull(seedPaths);
        const int Shown = 5;
        var names = string.Join(", ", seedPaths.Take(Shown).Select(path => "'" + UserView(path) + "'"));
        var more = seedPaths.Count > Shown ? FormattableString.Invariant($" and {seedPaths.Count - Shown} more") : string.Empty;
        var own = seedPaths.All(path => path.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("browser-profiles/", StringComparison.OrdinalIgnoreCase));
        return new(FormattableString.Invariant(
                $"Windows no longer gives back {seedPaths.Count} of the files iGloo copied to the installer partition: {names}{more}. ") +
            "A virus scanner removes a file it reports as a threat; Windows Security lists it under Virus & threat protection › Protection history. " +
            (own
                ? "Go back to Configuration and leave out the folder or browser that holds them, or move the files elsewhere, then continue."
                : "The installer cannot start without them. Read the reason there before you prepare again."));
    }

    // A selected file is shown as the user knows it: without the partition's folder for files, with backslashes.
    private static string UserView(string seedPath) =>
        (seedPath.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ? seedPath["files/".Length..] : seedPath)
        .Replace('/', '\\');
}
