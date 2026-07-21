using System.IO;
using System.Management;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

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
[SupportedOSPlatform("windows")]
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

        // Give the space back: an end user who removes Linux expects their disk
        // to grow, not a mysterious "unallocated" hole. A partition can only be
        // extended into ADJACENT trailing space and GetSupportedSize's SizeMax
        // is exactly that — so this self-guards: when the freed space doesn't
        // border the Windows partition, SizeMax barely moves and nothing happens.
        if (failures == 0)
        {
            foreach (var disk in installations.Select(i => i.DiskNumber)
                         .Concat(seedLeftovers.Select(s => s.DiskNumber))
                         .Distinct())
            {
                ct.ThrowIfCancellationRequested();
                TryReclaimFreedSpace(disk, progress);
            }
        }

        // Boot-menu hygiene: iGloo's one-shot entries are always ours to delete;
        // every Linux-classified entry goes only when no Linux remains to boot.
        foreach (var entry in EfiBootEntries.Enumerate(_logger))
        {
            if (EfiBootEntries.IsIglooDescription(entry.Description) ||
                (removingAllLinux && EfiBootEntries.IsLinuxDescription(entry.Description)))
                EfiBootEntries.Delete(entry.Index, _logger);
        }

        // ESP hygiene — the "mountvol S: /S && rmdir \EFI\<distro>" step people
        // otherwise do by hand. Whitelisted Linux loader folders only, and only
        // when no Linux remains that could still need them.
        if (removingAllLinux)
        {
            progress?.Report("Cleaning Linux boot files from the EFI partition…");
            CleanEfiSystemPartitions();
        }

        // Symmetry with DirectInstallService.SetRtcUniversalTime(): with the last
        // Linux gone, nothing needs the RTC in UTC anymore — restore Windows'
        // stock local-time behavior so the machine is left exactly as found.
        if (removingAllLinux)
            RestoreRtcLocalTime();

        if (failures > 0)
            throw new InvalidOperationException(
                $"{failures} partition(s) could not be deleted. Re-run the system check " +
                "to see the current state; a reboot may release in-use volumes.");

        progress?.Report("Removal complete.");
    }

    // ── Reclaim freed space ──────────────────────────────────────────────────

    /// <summary>
    /// Extends the disk's main lettered partition (C: on the Windows disk) into
    /// the space the removal just freed. MSFT_Partition.Resize grows the
    /// partition AND its filesystem. Best-effort: the space staying unallocated
    /// is an inconvenience, not a failure.
    /// </summary>
    private void TryReclaimFreedSpace(uint diskNumber, IProgress<string>? progress)
    {
        const long MiB = 1024L * 1024;
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results = searcher.Get();

            // Same heuristic as the shrink service: the largest lettered
            // partition is the disk's main data partition.
            ManagementObject? best = null;
            long bestSize = 0;
            char bestLetter = '\0';
            foreach (var p in results.Cast<ManagementObject>())
            {
                var dl = WmiValues.ToDriveLetter(p["DriveLetter"]);
                var size = dl == '\0' ? 0 : Convert.ToInt64(p["Size"]);
                if (size > bestSize)
                {
                    best?.Dispose();
                    (best, bestSize, bestLetter) = (p, size, dl);
                }
                else
                {
                    p.Dispose();
                }
            }
            if (best is null)
                return;

            using (best)
            {
                var supported = best.InvokeMethod("GetSupportedSize",
                    best.GetMethodParameters("GetSupportedSize"), null)!;
                if (Convert.ToUInt32(supported["ReturnValue"]) != 0)
                    return;

                var sizeMax = Convert.ToInt64(supported["SizeMax"]);
                var gainBytes = sizeMax - bestSize;
                if (gainBytes < 64 * MiB)
                    return;   // no adjacent space to grow into

                var gainGb = gainBytes / (1024.0 * MiB);
                progress?.Report($"Adding {gainGb:N1} GB back to {bestLetter}:…");

                var inParams = best.GetMethodParameters("Resize");
                inParams["Size"] = (ulong)sizeMax;
                var result = best.InvokeMethod("Resize", inParams, null)!;
                var returnValue = Convert.ToUInt32(result["ReturnValue"]);
                if (returnValue != 0)
                    _logger.LogWarning(
                        "Resize({Letter}:) to reclaim freed space returned {Code} (non-fatal) - " +
                        "the space stays unallocated", bestLetter, returnValue);
                else
                    _logger.LogInformation("Extended {Letter}: on disk {Disk} by {Gb:N1} GB",
                        bestLetter, diskNumber, gainGb);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not reclaim freed space on disk {Disk} (non-fatal)", diskNumber);
        }
    }

    // ── EFI System Partition cleanup ─────────────────────────────────────────

    /// <summary>
    /// Loader folders under \EFI\ that belong to Linux boot chains. STRICT
    /// whitelist — anything not listed here survives. \EFI\Microsoft and
    /// \EFI\BOOT are never candidates: Microsoft is Windows itself, and BOOT is
    /// the firmware fallback some machines boot Windows through.
    /// </summary>
    private static readonly string[] LinuxEfiFolders =
    [
        "ubuntu", "fedora", "debian", "linuxmint", "opensuse", "suse", "manjaro",
        "arch", "endeavouros", "garuda", "cachyos", "nobara", "zorin",
        "elementary", "neon", "kylin", "openkylin", "deepin", "uos", "bazzite",
        "mx", "mxlinux", "grub", "systemd", "centos", "rocky", "alma", "tuxedo",
        "solus", "gentoo", "slackware", "void", "nixos",
    ];

    /// <summary>Folder-name prefixes (systemd-boot installs use per-OS suffixes,
    /// e.g. "Pop_OS-1234-abcd").</summary>
    private static readonly string[] LinuxEfiFolderPrefixes = ["pop_os", "pop!_os"];

    /// <summary>
    /// Deletes whitelisted Linux loader folders from every EFI System Partition,
    /// plus systemd-boot's root \loader folder. The ESP has no drive letter but
    /// is reachable via its \\?\Volume{guid}\ access path when elevated — no
    /// mountvol needed. Best-effort: a leftover folder is cosmetic, so failures
    /// log a warning and never fail the removal.
    /// </summary>
    private void CleanEfiSystemPartitions()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNs,
                "SELECT AccessPaths FROM MSFT_Partition " +
                "WHERE GptType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'");
            using var results = searcher.Get();

            foreach (var esp in results.Cast<ManagementBaseObject>())
            {
                var volumePath = (esp["AccessPaths"] as string[])?
                    .FirstOrDefault(p => p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
                if (volumePath is null)
                    continue;

                CleanOneEsp(volumePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ESP cleanup failed (non-fatal) - Linux boot " +
                "folders may remain on the EFI partition");
        }
    }

    private void CleanOneEsp(string volumePath)
    {
        var efiRoot = Path.Combine(volumePath, "EFI");
        if (Directory.Exists(efiRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(efiRoot))
            {
                var name = Path.GetFileName(dir);
                var isLinux = LinuxEfiFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || LinuxEfiFolderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (!isLinux)
                    continue;

                TryDeleteEspFolder(dir);
            }
        }

        // systemd-boot keeps its menu config at the ESP root, outside \EFI.
        var loaderDir = Path.Combine(volumePath, "loader");
        if (Directory.Exists(loaderDir))
            TryDeleteEspFolder(loaderDir);
    }

    private void TryDeleteEspFolder(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted Linux boot folder {Dir} from the ESP", dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete {Dir} (non-fatal)", dir);
        }
    }

    /// <summary>Removes RealTimeIsUniversal (set by the install pipeline for
    /// dual-boot clock agreement) so a Linux-free machine behaves stock again.</summary>
    private void RestoreRtcLocalTime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", writable: true);
            if (key?.GetValue("RealTimeIsUniversal") is not null)
            {
                key.DeleteValue("RealTimeIsUniversal");
                _logger.LogInformation("RealTimeIsUniversal removed - Windows RTC back to local time");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove RealTimeIsUniversal (non-fatal)");
        }
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
