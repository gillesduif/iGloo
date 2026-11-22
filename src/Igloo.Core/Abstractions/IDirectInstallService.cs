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

    /// <summary>Removes the BCD entry of one registration and the firmware entries of its installer partition, once that partition is gone or its installer was started and left it behind.</summary>
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
    /// <summary>The installer partition still exists and its installer was not started; its boot entries and the task stay.</summary>
    PartitionPresent,
    /// <summary>The partition is gone but the BCD entry is still listed, or the listing could not be read; the task stays.</summary>
    EntriesRemain,
    /// <summary>The partition is gone; its boot entries and the task were removed.</summary>
    Removed,
    /// <summary>The installer was started and its partition is still there; its boot entries and the task were removed, the partition stays for a new attempt.</summary>
    RegistrationUndone,
}

/// <summary>How the provider's installer picks its target. Layouts it could misread are refused before Windows is resized.</summary>
/// <param name="MinimumRegionBytes">Smallest unallocated region the installer accepts.</param>
/// <param name="ScansAllDisks">The installer takes the largest unallocated region on any disk.</param>
/// <param name="UsesExistingEsp">The installer mounts an EFI system partition it finds on the target disk.</param>
public sealed record InstallerTargetRules(long MinimumRegionBytes, bool ScansAllDisks, bool UsesExistingEsp)
{
    /// <summary>The installer partitions only the unallocated space directly behind the installer partition, so iGloo decides its size by where it puts that partition.</summary>
    public bool BoundToInstallerPartition { get; init; }

    /// <summary>When the target disk has more than one EFI system partition, the installer mounts the first one and formats none.</summary>
    public bool MountsFirstEsp { get; init; }
}

/// <summary>Read-only capacity estimate; it does not authorize a partition change.</summary>
/// <param name="ShrinkableBytes">Room for the plan: the Windows shrink limit, or the unallocated region when that is larger.</param>
/// <param name="InstallerBytes">Installer partitions in addition to the requested Linux root.</param>
/// <param name="StagingBytes">Additional Windows space reserved for the pending migration package.</param>
public sealed record DirectInstallSpaceEstimate(long ShrinkableBytes, long InstallerBytes,
    long StagingBytes)
{
    /// <summary>The largest unallocated region on the disk; zero when there is none or it was not measured.</summary>
    public long UnallocatedBytes { get; init; }

    /// <summary>The installer media and seed go on one partition and no Linux partition is made beforehand.</summary>
    public bool SingleInstallerPartition { get; init; }

    /// <summary>The installer is confined to the requested size; false when it takes an unallocated region whole.</summary>
    public bool HonoursLinuxSize { get; init; }

    /// <summary>What a request leads to, by the rule the preparation applies to the live disk.</summary>
    public DirectInstallAllocation Allocate(long requestedLinuxBytes) =>
        requestedLinuxBytes > 0 && UnallocatedBytes - InstallerBytes >= requestedLinuxBytes
            ? new(false, HonoursLinuxSize ? requestedLinuxBytes : UnallocatedBytes - InstallerBytes)
            : new(true, requestedLinuxBytes);
}

/// <param name="ResizesWindows">False when the unallocated region holds the request and Windows is left as it is.</param>
/// <param name="LinuxBytes">What the Linux installer is given: the request, or all of the unallocated region left beside the installer partitions when the installer cannot be confined.</param>
public readonly record struct DirectInstallAllocation(bool ResizesWindows, long LinuxBytes);

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
