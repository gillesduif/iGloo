namespace Igloo.Core.Abstractions;

/// <summary>How Linux will coexist (or not) with existing data on the target disk.</summary>
public enum DiskInstallMode
{
    /// <summary>
    /// The entire target disk is erased and Linux is installed alone.
    /// Kickstart: <c>clearpart --drives=X --all --initlabel</c>.
    /// </summary>
    ReplaceDisk,

    /// <summary>
    /// The main Windows NTFS partition is shrunk to create free space, and Linux
    /// is installed in that space alongside Windows.
    /// Kickstart: <c>clearpart --none</c> (Anaconda uses the unpartitioned free space).
    /// iGloo shrinks the Windows partition during the USB-write step.
    /// </summary>
    DualBoot,
}
