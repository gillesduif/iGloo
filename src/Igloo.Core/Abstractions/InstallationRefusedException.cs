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

    /// <summary>The refusal when the signed list of the installer's boot files, or a file it names, cannot be fetched.</summary>
    public static InstallationRefusedException InstallerFilesUnreachable(Exception cause) =>
        new("The installer's boot files could not be fetched from the distribution's server. " +
            "Check your internet connection, then go back one step and continue again; if this returns, try again later.", cause);

    /// <summary>The refusal when that list carries no valid signature of the pinned key, or is the list of another release series.</summary>
    public static InstallationRefusedException InstallerFilesNotAuthentic() =>
        new("The list of the installer's boot files carries no valid signature of the distribution's key for this release. " +
            "Go back one step and continue again later; if this returns, install an updated iGloo Community.");

    /// <summary>The refusal when the signed list does not name the files where the provider says they are.</summary>
    public static InstallationRefusedException InstallerFilesNotListed() =>
        new("The distribution's signed list does not name the installer's boot files where iGloo reads them. " +
            "Install an updated iGloo Community, or go back and choose another distribution.");

    /// <summary>The refusal when a file on the server is not the one the signed list names.</summary>
    public static InstallationRefusedException InstallerFilesOutOfStep() =>
        new("The installer's boot files on the distribution's server are not the ones its signed list names. " +
            "The server may be in the middle of an update. Go back one step and continue again later.");

    /// <summary>The refusal when the release of the installer's kernel or of the image's kernel cannot be read.</summary>
    public static InstallationRefusedException InstallerKernelUnreadable(string? installerKernel, string? imageKernel) =>
        new(FormattableString.Invariant(
                $"The installer's boot files are for {Kernel(installerKernel)} and the downloaded image is for {Kernel(imageKernel)}. ") +
            "iGloo cannot tell whether the two belong together. " +
            "Install an updated iGloo Community, or go back and choose another distribution.");

    /// <summary>The refusal when the installer's kernel and the image's kernel are of different releases.</summary>
    public static InstallationRefusedException InstallerAndImageDiffer(string installerKernel, string imageKernel) =>
        new(FormattableString.Invariant(
                $"The installer's boot files are for kernel {installerKernel} and the downloaded image is for kernel {imageKernel}. ") +
            "The installer finds its drivers only on an image of its own release, and a distribution publishes the two some hours apart when it brings out an update. " +
            "Close iGloo Community and start it again, so that the image is checked anew; if this returns, try again in a few hours.");

    private static string Kernel(string? release) => release is null ? "a kernel iGloo could not read" : "kernel " + release;

    // A selected file is shown as the user knows it: without the partition's folder for files, with backslashes.
    private static string UserView(string seedPath) =>
        (seedPath.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ? seedPath["files/".Length..] : seedPath)
        .Replace('/', '\\');
}
