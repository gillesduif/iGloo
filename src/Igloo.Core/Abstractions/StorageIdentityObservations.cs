using System.Collections.Immutable;

namespace Igloo.Core.Abstractions;

public enum StorageIdentityStrength { ProviderUniqueIdentity, Reduced, Unavailable }

public sealed record DiskObservation(
    Observation<uint> Number, Observation<string> UniqueId, Observation<uint> UniqueIdFormat,
    Observation<string> SerialNumber, Observation<uint> BusType, Observation<string> FriendlyName,
    Observation<ulong> Size, Observation<uint> PartitionStyle, Observation<Guid> GptGuid,
    Observation<uint> LogicalSectorSize, Observation<uint> PhysicalSectorSize)
{
    public StorageIdentityStrength IdentityStrength => UniqueId.Availability != ObservationAvailability.Available ||
        string.IsNullOrWhiteSpace(UniqueId.Value) ? StorageIdentityStrength.Unavailable :
        UniqueIdFormat.Availability == ObservationAvailability.Available && UniqueIdFormat.Value is 2 or 3 or 8 &&
        BusType.Availability == ObservationAvailability.Available && BusType.Value is not (0 or 14 or 15)
        ? StorageIdentityStrength.ProviderUniqueIdentity : StorageIdentityStrength.Reduced;
}

public sealed record PartitionObservation(Observation<uint> DiskNumber, Observation<uint> Number,
    Observation<Guid> PartitionGuid, Observation<Guid> GptType, Observation<ulong> Offset, Observation<ulong> Size,
    Observation<bool> IsSystem, Observation<bool> IsBoot, Observation<bool> IsActive,
    Observation<char> DriveLetter, Observation<ImmutableArray<string>> AccessPaths);

public sealed record VolumeObservation(Observation<Guid> VolumeGuid, Observation<Guid> PartitionGuid,
    Observation<string> FileSystem, Observation<string> Label, Observation<char> DriveLetter,
    Observation<string> Status, Observation<bool> DirtyBitSet);

public sealed record WindowsStorageSnapshot(ImmutableArray<DiskObservation> Disks,
    ImmutableArray<PartitionObservation> Partitions, ImmutableArray<VolumeObservation> Volumes);

public sealed record SupportedSizeObservation(ulong Minimum, ulong Maximum);
public sealed record BitLockerVolumeObservation(Guid VolumeGuid, Observation<char> DriveLetter,
    Observation<uint> ConversionStatus, Observation<uint> ProtectionStatus, Observation<uint> LockStatus,
    Observation<uint> EncryptionMethod);

public static class WindowsVolumeIdentity
{
    public static bool TryParse(string? path, out Guid id)
    {
        id = Guid.Empty;
        const string prefix = @"\\?\Volume{";
        return path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(@"}\", StringComparison.Ordinal) && path.Length == prefix.Length + 38 &&
            Guid.TryParseExact(path.Substring(prefix.Length, 36), "D", out id) && id != Guid.Empty;
    }
}
