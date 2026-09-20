using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed class WindowsStorageReader : IWindowsStorageReader
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public WindowsStorageBatch ReadDisks() => Query(StorageNamespace,
        "SELECT Number, FriendlyName, Size, AllocatedSize, PartitionStyle FROM MSFT_Disk");

    public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false)
    {
        var filters = new List<string>();
        if (diskNumber.HasValue) filters.Add(FormattableString.Invariant($"DiskNumber = {diskNumber.Value}"));
        if (partitionNumber.HasValue) filters.Add(FormattableString.Invariant($"PartitionNumber = {partitionNumber.Value}"));
        if (efiOnly) filters.Add("GptType = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'");
        return Query(StorageNamespace, "SELECT * FROM MSFT_Partition" +
            (filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters)));
    }

    public WindowsStorageBatch ReadVolumes(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter)) return new([], new ManagementException("Invalid drive-letter observation."));
        return Query(@"root\cimv2", $"SELECT DeviceID, DriveLetter, FileSystem, Label FROM Win32_Volume WHERE DriveLetter = '{driveLetter}:'");
    }

    public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false)
    {
        ArgumentNullException.ThrowIfNull(partition);
        try
        {
            using var target = new ManagementObject(partition.ObjectPath);
            return ReadSupportedSize(target, explicitParameters);
        }
        catch (Exception ex) when (IsObservationError(ex)) { return new(null, ex); }
    }

    // Existing mutation services can retain their selected WMI object and sequencing.
    internal static WindowsObservation<WindowsStorageRow> ReadSupportedSize(ManagementObject target, bool explicitParameters = false)
    {
        try
        {
            using var parameters = explicitParameters ? target.GetMethodParameters("GetSupportedSize") : null;
            using var result = target.InvokeMethod("GetSupportedSize", parameters, null);
            return new(result is null ? null : Copy(result));
        }
        catch (Exception ex) when (IsObservationError(ex)) { return new(null, ex); }
    }

    internal static WindowsStorageBatch Query(string scope, string query)
    {
        var rows = new List<WindowsStorageRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject row in results)
                using (row) rows.Add(Copy(row));
            return new(rows);
        }
        catch (Exception ex) when (IsObservationError(ex)) { return new(rows, ex); }
    }

    private static WindowsStorageRow Copy(ManagementBaseObject row) => new(
        row.Properties.Cast<PropertyData>().ToImmutableDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase),
        (row as ManagementObject)?.Path.Path);

    internal static bool IsObservationError(Exception ex) => ex is ManagementException or COMException or
        FormatException or OverflowException or InvalidCastException or InvalidOperationException;

}
