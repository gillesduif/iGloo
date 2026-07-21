namespace Igloo.Core.Abstractions;

/// <summary>
/// Stages the selected distro's installer directly onto a temporary FAT32 partition
/// carved from the target disk; no USB drive required. The distro's
/// <see cref="InstallerBootSpec"/> tells the pipeline what to extract, download,
/// inject and boot. Only applicable for <see cref="DiskInstallMode.DualBoot"/>.
/// </summary>
public interface IDirectInstallService
{
    /// <summary>
    /// Creates the OEMDRV temp partition on the disk, copies the ISO and
    /// migration artefacts onto it, and configures a GRUB2 EFI that boots the
    /// distro's installer. The Windows partition shrink is also performed here.
    /// </summary>
    Task PrepareAsync(
        int diskNumber,
        long linuxSizeBytes,
        string isoPath,
        string stagingDirectory,
        InstallerBootSpec bootSpec,
        string? stage2Url = null,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the UEFI <c>BootNext</c> NVRAM variable so the firmware boots
    /// the GRUB installer exactly once on the next reboot, then returns.
    /// </summary>
    Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);
}

public enum DirectInstallPhase
{
    ShrinkingPartition,
    CreatingPartition,
    CopyingIso,
    CopyingFiles,
    ConfiguringGrub,
    RegisteringBootEntry,
    Complete,
}

/// <summary>Progress snapshot reported during direct-install preparation.</summary>
public sealed record DirectInstallProgress(
    DirectInstallPhase Phase,
    long BytesWritten = 0,
    long BytesTotal = 0,
    string? Message = null);
