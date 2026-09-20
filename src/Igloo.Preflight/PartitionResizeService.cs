using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public sealed class PartitionResizeService : IPartitionResizeService
{
    private readonly IWindowsStorageReader _storage;

    private readonly ILogger<PartitionResizeService> _logger;

    public PartitionResizeService(ILogger<PartitionResizeService> logger) : this(logger, new WindowsStorageReader()) { }
    public PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage)
    { _logger = logger; _storage = storage; }

    //   IPartitionResizeService                        ─

    public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default)
        => Task.Run(() => GetShrinkableSpace(diskNumber), ct);

    public Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Shrink(diskNumber, linuxSizeBytes, progress, ct), ct);

    //   Private helpers                            ─

    private long GetShrinkableSpace(int diskNumber)
    {
        var (_, shrinkable) = FindNtfsPartition(diskNumber);
        return shrinkable;
    }

    private void Shrink(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        const long MiB = 1024L * 1024;

        progress?.Report("Querying Windows partition layout…");
        _logger.LogInformation(
            "Partition resize: disk {Disk}, need {LinuxMiB} MiB for Linux",
            diskNumber, linuxSizeBytes / MiB);

        var (selected, shrinkable) = FindNtfsPartition(diskNumber);
        if (selected is null)
            throw new InvalidOperationException(
                $"No shrinkable NTFS partition found on disk {diskNumber}.");

        ct.ThrowIfCancellationRequested();

        if (shrinkable < linuxSizeBytes)
            throw new InvalidOperationException(
                $"Not enough shrinkable space: need {linuxSizeBytes / MiB} MiB, " +
                $"available {shrinkable / MiB} MiB.");

        // Obtain precise size limits again on the selected mutation target, as before.
        using var mo = new ManagementObject(selected.ObjectPath);
        var outSizes = WindowsStorageReader.ReadSupportedSize(mo).ValueOrThrow()!;
        var sizeMin = Convert.ToInt64(outSizes["SizeMin"], CultureInfo.InvariantCulture);
        var sizeMax = Convert.ToInt64(outSizes["SizeMax"], CultureInfo.InvariantCulture);

        // New size = current size − linux allocation, aligned down to 1 MiB.
        long newSize = sizeMax - linuxSizeBytes;
        newSize = (newSize / MiB) * MiB;          // 1 MiB align

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

        var inParams = mo.GetMethodParameters("Resize");
        inParams["Size"] = (ulong)newSize;
        var result = mo.InvokeMethod("Resize", inParams, null)!;
        var returnVal = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);

        if (returnVal != 0)
            throw new InvalidOperationException(
                $"MSFT_Partition.Resize() failed with return code {returnVal}. " +
                "Try running CHKDSK on the Windows partition and retrying.");

        _logger.LogInformation("Partition resize succeeded");
        progress?.Report("Windows partition shrunk successfully.");
    }

    internal static bool HasCandidateLetter(char letter) => letter != '\0';
    internal static bool ImprovesCandidate(long delta, long best) => delta > best;

    internal (WindowsStorageRow? row, long shrinkable) FindNtfsPartition(int diskNumber)
    {
        try
        {
            WindowsStorageRow? best = null;
            long bestShrinkable = 0;

            foreach (var p in _storage.ReadPartitions(diskNumber).RowsOrThrow())
            {
                // We only want to resize the main NTFS data partition.
                // Heuristic: pick the largest partition that has a drive letter
                // and a positive SizeMin/SizeMax delta.
                char dl = WmiValues.ToDriveLetter(p["DriveLetter"]);
                if (!HasCandidateLetter(dl))
                    continue;

                try
                {
                    var outSizes = _storage.ReadSupportedSize(p).ValueOrThrow();
                    if (outSizes is null)
                        continue;

                    var returnValue = Convert.ToUInt32(outSizes["ReturnValue"], CultureInfo.InvariantCulture);
                    if (returnValue != 0)
                        continue;

                    var sizeMin = Convert.ToInt64(outSizes["SizeMin"], CultureInfo.InvariantCulture);
                    var sizeMax = Convert.ToInt64(outSizes["SizeMax"], CultureInfo.InvariantCulture);
                    var shrinkable = sizeMax - sizeMin;

                    if (ImprovesCandidate(shrinkable, bestShrinkable))
                    {
                        best = p;
                        bestShrinkable = shrinkable;
                    }
                }
                catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "GetSupportedSize skipped for drive {Letter}", dl);
                }
            }

            return (best, bestShrinkable);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "FindNtfsPartition failed for disk {Disk}", diskNumber);
            return (null, 0);
        }
    }
}
