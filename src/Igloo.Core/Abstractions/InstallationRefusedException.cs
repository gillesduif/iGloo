namespace Igloo.Core.Abstractions;

/// <summary>A refusal with a heading that states the condition and a message that states what resolves it; raised before Windows is resized wherever the cause can be known by then.</summary>
public sealed class InstallationRefusedException : InvalidOperationException
{
    private const string DefaultHeading = "Preparation failed";

    public InstallationRefusedException() { }

    public InstallationRefusedException(string message) : base(message) { }

    public InstallationRefusedException(string message, Exception innerException) : base(message, innerException) { }

    public InstallationRefusedException(string heading, string message) : base(message) => Heading = heading;

    public InstallationRefusedException(string heading, string message, Exception? innerException)
        : base(message, innerException) => Heading = heading;

    /// <summary>The condition, for the heading above the message.</summary>
    public string Heading { get; } = DefaultHeading;

    /// <summary>The refusal for a selected file that cannot go onto a FAT32 installer partition, naming the file.</summary>
    public static InstallationRefusedException FileTooLargeForInstallerPartition(string path, long bytes) =>
        new("File exceeds the 4 GB limit",
            FormattableString.Invariant($"'{path}' is {bytes / (1024.0 * 1024 * 1024):N1} GB. Exclude the file and try again."));

    /// <summary>The refusal for copied files that the installer partition no longer gives back, naming them by their path on it.</summary>
    public static InstallationRefusedException FilesLostFromInstallerPartition(IReadOnlyList<string> seedPaths)
    {
        ArgumentNullException.ThrowIfNull(seedPaths);
        const int Shown = 5;
        var names = string.Join(", ", seedPaths.Take(Shown).Select(path => "'" + UserView(path) + "'"));
        var more = seedPaths.Count > Shown ? FormattableString.Invariant($" and {seedPaths.Count - Shown} more") : string.Empty;
        var own = seedPaths.All(path => path.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("browser-profiles/", StringComparison.OrdinalIgnoreCase));
        return own
            ? new("Copied files are missing",
                $"{names}{more} may have been removed by antivirus software. Exclude the files and try again.")
            : new("Installer files are missing",
                $"{names}{more} may have been removed by antivirus software. Check the Protection history and try again.");
    }

    /// <summary>The refusal when the signed list of the installer's boot files, or a file it names, cannot be fetched.</summary>
    public static InstallationRefusedException InstallerFilesUnreachable(Exception cause) =>
        new("Installer files could not be downloaded", "Check the network connection and try again.", cause);

    /// <summary>The refusal when that list carries no valid signature of the pinned key, or is the list of another release series.</summary>
    public static InstallationRefusedException InstallerFilesNotAuthentic() =>
        new("Installer files could not be verified", "The signature is not valid. Try again later.");

    /// <summary>The refusal when the signed list does not name the files where the provider says they are.</summary>
    public static InstallationRefusedException InstallerFilesNotListed() =>
        new("Installer files not found",
            "The current release of this distribution is not supported by this version. Update iGloo Community and try again.");

    /// <summary>The refusal when a file on the server is not the one the signed list names.</summary>
    public static InstallationRefusedException InstallerFilesOutOfStep() =>
        new("Installer files do not match the signed list", "The distribution server may be updating. Try again later.");

    /// <summary>The refusal when the release of the installer's kernel or of the image's kernel cannot be read; the inner exception names what was read.</summary>
    public static InstallationRefusedException InstallerKernelUnreadable(string? installerKernel, string? imageKernel) =>
        new("Installer version could not be verified", "Update iGloo Community and try again.",
            new InvalidDataException(FormattableString.Invariant(
                $"Installer kernel: {installerKernel ?? "unreadable"}. Image kernel: {imageKernel ?? "unreadable"}.")));

    /// <summary>The refusal when the installer's kernel and the image's kernel are of different releases.</summary>
    public static InstallationRefusedException InstallerAndImageDiffer(string installerKernel, string imageKernel) =>
        new("Installer and image version mismatch", FormattableString.Invariant(
            $"The installer uses kernel {installerKernel} and the image uses kernel {imageKernel}. Restart iGloo Community and try again."));

    // A selected file is shown as the user knows it: without the partition's folder for files, with backslashes.
    private static string UserView(string seedPath) =>
        (seedPath.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ? seedPath["files/".Length..] : seedPath)
        .Replace('/', '\\');
}
