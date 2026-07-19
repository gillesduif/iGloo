using System.Management;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>
/// Removes Linux installations (and leftover iGloo seed partitions) from Windows:
/// deletes the partitions via <c>MSFT_Partition.DeleteObject()</c> and cleans the
/// corresponding UEFI boot entries so the firmware menu doesn't keep dead loaders.
///
/// Safety rules:
///   * only partitions the preflight checker classified by GPT type GUID (Linux)
///     or exact iGloo seed label are ever passed in — this service never scans;
///   * the EFI System Partition is never touched (a Linux \EFI\&lt;distro&gt; folder
///     of a few MB may remain — harmless once its boot entry is gone);
///   * boot entries: iGloo's own entries are always removed; a Linux entry only
///     when unambiguously paired, or all of them when the last install goes.
/// The freed space is left unallocated for the user (or a future iGloo install).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LinuxRemovalService : ILinuxRemovalService
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    private readonly ILogger<LinuxRemovalService> _logger;

    public LinuxRemovalService(ILogger<LinuxRemovalService> logger) => _logger = logger;

    public Task RemoveAsync(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Remove(installations, seedLeftovers, removingAllLinux, progress, ct), ct);

    private void Remove(IReadOnlyList<LinuxInstallation> installations,
        IReadOnlyList<SeedLeftover> seedLeftovers, bool removingAllLinux,
        IProgress<string>? progress, CancellationToken ct)
    {
        var failures = 0;

        foreach (var install in installations)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Removing {install.DisplayName} from {install.DiskModel}…");

            // Delete back-to-front so partition numbers stay valid even on
            // providers that renumber after a deletion.
            foreach (var p in install.Partitions.OrderByDescending(p => p.OffsetBytes))
            {
                if (!DeletePartition(install.DiskNumber, p.Index))
                    failures++;
            }

            if (install.FirmwareEntryIndex is { } index)
                EfiBootEntries.Delete(index, _logger);
        }

        foreach (var leftover in seedLeftovers)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Removing iGloo installer partition {leftover.Partition.Label}…");
            if (!DeletePartition(leftover.DiskNumber, leftover.Partition.Index))
                failures++;
        }

        // Boot-menu hygiene: iGloo's one-shot entries are always ours to delete;
        // every Linux-classified entry goes only when no Linux remains to boot.
        foreach (var entry in EfiBootEntries.Enumerate(_logger))
        {
            if (EfiBootEntries.IsIglooDescription(entry.Description) ||
                (removingAllLinux && EfiBootEntries.IsLinuxDescription(entry.Description)))
                EfiBootEntries.Delete(entry.Index, _logger);
        }

        if (failures > 0)
            throw new InvalidOperationException(
                $"{failures} partition(s) could not be deleted. Re-run the system check " +
                "to see the current state; a reboot may release in-use volumes.");

        progress?.Report("Removal complete.");
    }

    private bool DeletePartition(uint diskNumber, int partitionNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} " +
                $"AND PartitionNumber = {partitionNumber}");
            using var results = searcher.Get();
            var partition = results.Cast<ManagementObject>().FirstOrDefault();
            if (partition is null)
            {
                _logger.LogWarning("Partition {Disk}:{Part} no longer exists - skipped",
                    diskNumber, partitionNumber);
                return true;
            }

            using (partition)
            {
                var result = partition.InvokeMethod("DeleteObject", partition.GetMethodParameters("DeleteObject"), null)!;
                var returnValue = Convert.ToUInt32(result["ReturnValue"]);
                if (returnValue != 0)
                {
                    _logger.LogError("MSFT_Partition.DeleteObject({Disk}:{Part}) returned {Code}",
                        diskNumber, partitionNumber, returnValue);
                    return false;
                }
            }

            _logger.LogInformation("Deleted partition {Disk}:{Part}", diskNumber, partitionNumber);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeletePartition({Disk}:{Part}) failed", diskNumber, partitionNumber);
            return false;
        }
    }
}
