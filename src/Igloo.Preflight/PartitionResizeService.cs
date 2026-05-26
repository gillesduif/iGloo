using System.Management;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>
/// Shrinks the main Windows NTFS partition on a target disk to create unpartitioned
/// free space that Anaconda can use for a Linux dual-boot installation.
///
/// Uses <c>ROOT\Microsoft\Windows\Storage</c> WMI — specifically the
/// <c>MSFT_Partition.GetSupportedSize()</c> and <c>MSFT_Partition.Resize()</c> methods.
/// These operations require the calling process to be running as Administrator.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PartitionResizeService : IPartitionResizeService
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    private readonly ILogger<PartitionResizeService> _logger;

    public PartitionResizeService(ILogger<PartitionResizeService> logger) => _logger = logger;

    // ── IPartitionResizeService ───────────────────────────────────────────────

    public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default)
        => Task.Run(() => GetShrinkableSpace(diskNumber), ct);

    public Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Shrink(diskNumber, linuxSizeBytes, progress, ct), ct);

    // ── Private helpers ───────────────────────────────────────────────────────

    private long GetShrinkableSpace(int diskNumber)
    {
        var (_, shrinkable) = FindNtfsPartition(diskNumber);
        return shrinkable;
    }

    /// <summary>
    /// Finds the largest NTFS partition on the disk, invokes
    /// <c>GetSupportedSize()</c> to learn the minimum shrink target, then
    /// calls <c>Resize()</c> to free exactly <paramref name="linuxSizeBytes"/>.
    /// Alignment: rounds the new partition size DOWN to the nearest 1 MiB boundary.
    /// </summary>
    private void Shrink(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        const long MiB = 1024L * 1024;

        progress?.Report("Querying Windows partition layout…");
        _logger.LogInformation(
            "Partition resize: disk {Disk}, need {LinuxMiB} MiB for Linux",
            diskNumber, linuxSizeBytes / MiB);

        var (mo, shrinkable) = FindNtfsPartition(diskNumber);
        if (mo is null)
            throw new InvalidOperationException(
                $"No shrinkable NTFS partition found on disk {diskNumber}.");

        ct.ThrowIfCancellationRequested();

        if (shrinkable < linuxSizeBytes)
            throw new InvalidOperationException(
                $"Not enough shrinkable space: need {linuxSizeBytes / MiB} MiB, " +
                $"available {shrinkable / MiB} MiB.");

        // Obtain precise size limits.
        var outSizes = mo.InvokeMethod("GetSupportedSize", null, null)!;
        var sizeMin  = Convert.ToInt64(outSizes["SizeMin"]);
        var sizeMax  = Convert.ToInt64(outSizes["SizeMax"]);

        // New size = current size − linux allocation, aligned down to 1 MiB.
        long newSize  = sizeMax - linuxSizeBytes;
        newSize       = (newSize / MiB) * MiB;          // 1 MiB align

        if (newSize < sizeMin)
        {
            _logger.LogWarning(
                "Aligned new size {New} MiB is below minimum {Min} MiB; clamping to minimum",
                newSize / MiB, sizeMin / MiB);
            newSize = (sizeMin / MiB + 1) * MiB;        // round up to next MiB above minimum
        }

        _logger.LogInformation(
            "Resizing NTFS partition: {Old} MiB → {New} MiB (freeing {Free} MiB for Linux)",
            sizeMax / MiB, newSize / MiB, (sizeMax - newSize) / MiB);

        progress?.Report($"Shrinking Windows partition from {sizeMax / MiB:N0} MiB to {newSize / MiB:N0} MiB…");

        ct.ThrowIfCancellationRequested();

        var inParams  = mo.GetMethodParameters("Resize");
        inParams["Size"] = (ulong)newSize;
        var result    = mo.InvokeMethod("Resize", inParams, null)!;
        var returnVal = Convert.ToUInt32(result["ReturnValue"]);

        if (returnVal != 0)
            throw new InvalidOperationException(
                $"MSFT_Partition.Resize() failed with return code {returnVal}. " +
                "Try running CHKDSK on the Windows partition and retrying.");

        _logger.LogInformation("Partition resize succeeded");
        progress?.Report("Windows partition shrunk successfully.");
    }

    /// <summary>
    /// Returns the WMI <c>ManagementObject</c> and shrinkable byte count for the
    /// largest NTFS partition on <paramref name="diskNumber"/>.
    /// Returns <c>(null, 0)</c> if none is found.
    /// </summary>
    private (ManagementObject? mo, long shrinkable) FindNtfsPartition(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                StorageNamespace,
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
            using var results = searcher.Get();

            ManagementObject? best = null;
            long              bestShrinkable = 0;

            foreach (ManagementObject p in results.Cast<ManagementObject>())
            {
                // We only want to resize the main NTFS data partition.
                // Heuristic: pick the largest partition that has a drive letter
                // and a positive SizeMin/SizeMax delta.
                char dl = p["DriveLetter"] switch
                {
                    char c           => c,
                    ushort u when u > 0 => (char)u,
                    string s when s.Length > 0 => s[0],
                    _                => '\0',
                };
                if (dl == '\0') continue;

                try
                {
                    var outSizes = p.InvokeMethod("GetSupportedSize", null, null);
                    if (outSizes is null) continue;

                    var returnValue = Convert.ToUInt32(outSizes["ReturnValue"]);
                    if (returnValue != 0) continue;

                    var sizeMin = Convert.ToInt64(outSizes["SizeMin"]);
                    var sizeMax = Convert.ToInt64(outSizes["SizeMax"]);
                    var shrinkable = sizeMax - sizeMin;

                    if (shrinkable > bestShrinkable)
                    {
                        best?.Dispose();
                        best           = (ManagementObject)p.Clone();
                        bestShrinkable = shrinkable;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetSupportedSize skipped for drive {Letter}", dl);
                }
                finally
                {
                    p.Dispose();
                }
            }

            return (best, bestShrinkable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindNtfsPartition failed for disk {Disk}", diskNumber);
            return (null, 0);
        }
    }
}
