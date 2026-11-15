namespace Igloo.Core.Abstractions;

public interface IDirectInstallService
{
    Observation<bool> PreparationAvailability { get; }

    Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(int diskNumber, string isoPath,
        InstallerBootSpec bootSpec, long stagedBytes, CancellationToken ct = default) =>
        throw new NotSupportedException("Direct installation space measurement is unavailable.");

    /// <summary>Reads the installer image ahead of the space check, which then reuses the result.</summary>
    Task WarmImageMeasurementAsync(string isoPath, InstallerBootSpec bootSpec, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Asks Windows for the shrink limit of a disk ahead of the space check, which then reuses the answer.</summary>
    Task WarmShrinkEstimateAsync(int diskNumber, CancellationToken ct = default) => Task.CompletedTask;

    Task PrepareAsync(
        int diskNumber,
        long linuxSizeBytes,
        string isoPath,
        string stagingDirectory,
        InstallerBootSpec bootSpec,
        InstallerTargetRules targetRules,
        Uri? stage2Url = null,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);

    Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Removes the BCD entry of one registration and the firmware entries of its installer partition once that partition is gone.</summary>
    Task<BootCleanupOutcome> CleanUpBootEntriesAsync(Guid installerPartitionGuid, Guid bcdEntry,
        CancellationToken ct = default) => Task.FromResult(BootCleanupOutcome.NotRead);
}

/// <summary>The command line of the cleanup task: this argument, the installer partition's GUID, the BCD entry's identifier.</summary>
public static class BootCleanup
{
    public const string Argument = "--boot-cleanup";
}

/// <summary>What the boot-entry cleanup after an installation found and did.</summary>
public enum BootCleanupOutcome
{
    /// <summary>The disks could not be read; nothing was removed and the task stays.</summary>
    NotRead,
    /// <summary>The installer partition still exists; its boot entries and the task stay.</summary>
    PartitionPresent,
    /// <summary>The partition is gone but the BCD entry is still listed, or the listing could not be read; the task stays.</summary>
    EntriesRemain,
    /// <summary>The partition is gone; its boot entries and the task were removed.</summary>
    Removed,
}

/// <summary>How the provider's installer picks its target. Layouts it could misread are refused before Windows is resized.</summary>
/// <param name="MinimumRegionBytes">Smallest unallocated region the installer accepts.</param>
/// <param name="ScansAllDisks">The installer takes the largest unallocated region on any disk.</param>
/// <param name="UsesExistingEsp">The installer mounts an EFI system partition it finds on the target disk.</param>
public sealed record InstallerTargetRules(long MinimumRegionBytes, bool ScansAllDisks, bool UsesExistingEsp);

/// <summary>Read-only capacity estimate; it does not authorize a partition change.</summary>
/// <param name="ShrinkableBytes">Current Windows boot-volume shrink limit.</param>
/// <param name="InstallerBytes">Installer partitions in addition to the requested Linux root.</param>
/// <param name="StagingBytes">Additional Windows space reserved for the pending migration package.</param>
public sealed record DirectInstallSpaceEstimate(long ShrinkableBytes, long InstallerBytes,
    long StagingBytes);

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


public sealed record DirectInstallProgress(
    DirectInstallPhase Phase,
    long BytesWritten = 0,
    long BytesTotal = 0,
    string? Message = null);
