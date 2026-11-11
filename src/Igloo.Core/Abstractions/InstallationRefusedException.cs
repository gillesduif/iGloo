namespace Igloo.Core.Abstractions;

/// <summary>A refusal raised before Windows is resized. The message tells the user what to do next.</summary>
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
}
