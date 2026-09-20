using System.Collections.Immutable;
using System.Globalization;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

// Pure enrichment of the canonical reader's rows. Community keeps its existing projection.
public static class StorageObservationProjection
{
    internal static Observation<uint> Number(WindowsStorageRow row, string name) => row.Fact(name, v => Convert.ToUInt32(v, CultureInfo.InvariantCulture));
    internal static Observation<ulong> ByteCount(WindowsStorageRow row, string name) => row.Fact(name, v => Convert.ToUInt64(v, CultureInfo.InvariantCulture));
    internal static Observation<string> Text(WindowsStorageRow row, string name) => row.Fact(name, v => (string)v);
    internal static Observation<Guid> GuidFact(WindowsStorageRow row, string name) => row.Fact(name, v =>
        Guid.TryParse((string)v, out var id) && id != Guid.Empty ? id : throw new FormatException("Invalid GUID."));
    internal static Observation<char> Letter(WindowsStorageRow row) => row.Fact("DriveLetter", v => WmiValues.ToDriveLetter(v));

    public static DiskObservation Disk(WindowsStorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(Number(row, "Number"), Text(row, "UniqueId"),
        Number(row, "UniqueIdFormat"), Text(row, "SerialNumber"), Number(row, "BusType"), Text(row, "FriendlyName"),
        ByteCount(row, "Size"), Number(row, "PartitionStyle"), GuidFact(row, "Guid"), Number(row, "LogicalSectorSize"), Number(row, "PhysicalSectorSize"));
    }

    public static PartitionObservation Partition(WindowsStorageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(Number(row, "DiskNumber"), Number(row, "PartitionNumber"),
        GuidFact(row, "Guid"), GuidFact(row, "GptType"), ByteCount(row, "Offset"), ByteCount(row, "Size"),
        row.Fact("IsSystem", v => (bool)v), row.Fact("IsBoot", v => (bool)v), row.Fact("IsActive", v => (bool)v),
        Letter(row), row.Fact("AccessPaths", v => ((string[])v).ToImmutableArray()));
    }

    public static VolumeObservation Volume(WindowsStorageRow row, ImmutableArray<PartitionObservation> partitions)
    {
        ArgumentNullException.ThrowIfNull(row);
        var id = row.Fact("DeviceID", v => WindowsVolumeIdentity.TryParse((string)v, out var guid) ? guid : throw new FormatException("Invalid volume identity."));
        var owner = Observations.Failure<Guid>(ObservationAvailability.Unavailable, "VolumeOwnerNotProven");
        if (id.Availability == ObservationAvailability.Available)
        {
            var matches = partitions.Where(p => p.AccessPaths.Availability == ObservationAvailability.Available &&
                p.AccessPaths.Value.Any(path => WindowsVolumeIdentity.TryParse(path, out var guid) && guid == id.Value)).ToArray();
            if (matches.Length == 1) owner = matches[0].PartitionGuid;
            if (matches.Length > 1) owner = Observations.Failure<Guid>(ObservationAvailability.Ambiguous, "MultipleVolumeOwners");
        }
        return new(id, owner, Text(row, "FileSystem"), Text(row, "Label"), Letter(row), Text(row, "Status"), row.Fact("DirtyBitSet", v => (bool)v));
    }

    public static Observation<SupportedSizeObservation> SupportedSize(WindowsObservation<WindowsStorageRow> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Availability != ObservationAvailability.Available)
            return Observations.Failure<SupportedSizeObservation>(observed.Availability, "SupportedSizeUnavailable");
        var row = observed.Value!;
        var code = Number(row, "ReturnValue");
        if (code.Availability != ObservationAvailability.Available)
            return Observations.Failure<SupportedSizeObservation>(code.Availability, "SupportedSizeReturnMissing");
        var status = ObservationErrors.SupportedSizeReturn(code.Value);
        if (status != ObservationAvailability.Available) return Observations.Failure<SupportedSizeObservation>(status, "SupportedSizeProviderRejected");
        var minimum = ByteCount(row, "SizeMin");
        var maximum = ByteCount(row, "SizeMax");
        if (minimum.Availability != ObservationAvailability.Available || maximum.Availability != ObservationAvailability.Available)
            return Observations.Failure<SupportedSizeObservation>(ObservationAvailability.Unavailable, "SupportedSizeFieldsMissing");
        return maximum.Value >= minimum.Value ? Observations.Available<SupportedSizeObservation>(new(minimum.Value, maximum.Value)) :
            Observations.Failure<SupportedSizeObservation>(ObservationAvailability.Ambiguous, "SupportedSizeInconsistent");
    }
}
