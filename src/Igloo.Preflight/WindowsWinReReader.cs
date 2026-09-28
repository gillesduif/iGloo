using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

// ReAgent.xml is read as typed source evidence. No reagentc text or undocumented DLL entry point.
public sealed class WindowsWinReReader
{
    private readonly Func<Observation<ImmutableArray<byte>>> _read;
    private readonly Func<Guid, string, Observation<CanonicalFileIdentityV1>> _readFile;
    public WindowsWinReReader() : this(ReadXml, WindowsRecoveryPathReader.ReadFileIdentity) { }
    internal WindowsWinReReader(Func<Observation<ImmutableArray<byte>>> read,
        Func<Guid, string, Observation<CanonicalFileIdentityV1>> readFile) { _read = read; _readFile = readFile; }

    public Observation<WinReConfigurationEvidence> ReadConfiguration() => WinReConfigurationParser.Parse(_read());

    public WinReConfigurationV1 Capture(Observation<WindowsStorageSnapshot> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var source = ReadConfiguration();
        if (source.Availability != ObservationAvailability.Available)
        {
            // Missing XML does not prove that Windows RE itself is disabled or absent.
            var state = source.Availability == ObservationAvailability.Absent ? ObservationAvailability.Unavailable : source.Availability;
            var code = source.Availability == ObservationAvailability.Absent ? "WinReConfigurationFileAbsent" : source.Code ?? "WinReConfigurationReadFailed";
            return new(1, Observations.Failure<bool>(state, code), Observations.Failure<Guid>(state, code),
                Observations.Failure<CanonicalVolumeIdentityV1>(state, code), Observations.Failure<CanonicalFileIdentityV1>(state, code));
        }
        var data = source.Value;
        var volume = CorrelateLocation(storage, data.Location);
        var image = volume.Availability == ObservationAvailability.Available
            ? _readFile(volume.Value.VolumeGuid, data.Location.Value.Directory + @"\Winre.wim")
            : Observations.Failure<CanonicalFileIdentityV1>(volume.Availability, volume.Code ?? "WinReLocationUnavailable");
        // A versioned XML integer is not a documented enabled-state contract. No guessed 0/1 conversion.
        return new(1, Observations.Failure<bool>(ObservationAvailability.Unsupported, "WinReInstallStateSemanticsUnproven"),
            data.RecoveryLoaderId, volume, image);
    }

    internal static Observation<CanonicalVolumeIdentityV1> CorrelateLocation(Observation<WindowsStorageSnapshot> storage,
        Observation<WinReLocationEvidence> location)
    {
        if (location.Availability != ObservationAvailability.Available) return Failure(location.Availability, location.Code ?? "WinReLocationUnavailable");
        if (storage.Availability != ObservationAvailability.Available) return Failure(storage.Availability, "WinReStorageUnavailable");
        var inventory = storage.Value;
        if (inventory.Disks.IsDefault || inventory.Partitions.IsDefault || inventory.Volumes.IsDefault) return Failure(ObservationAvailability.Ambiguous, "WinReInventoryMalformed");
        var diskFailure = inventory.Disks.Select(d => d.GptGuid.Availability)
            .FirstOrDefault(s => s is not (ObservationAvailability.Available or ObservationAvailability.Absent), ObservationAvailability.Available);
        if (diskFailure != ObservationAvailability.Available) return Failure(diskFailure, "WinReDiskIdentityUnavailable");
        var disks = inventory.Disks.Where(d => d.GptGuid.Availability == ObservationAvailability.Available && d.GptGuid.Value == location.Value.DiskGuid).ToArray();
        if (disks.Length != 1) return Failure(disks.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "WinReDiskNotUnique");
        if (disks[0].Number.Availability != ObservationAvailability.Available) return Failure(disks[0].Number.Availability, "WinReDiskJoinUnavailable");
        var partitionFailure = inventory.Partitions.Select(p => p.DiskNumber.Availability != ObservationAvailability.Available
            ? p.DiskNumber.Availability : p.DiskNumber.Value == disks[0].Number.Value ? p.Offset.Availability : ObservationAvailability.Available)
            .FirstOrDefault(s => s != ObservationAvailability.Available, ObservationAvailability.Available);
        if (partitionFailure != ObservationAvailability.Available) return Failure(partitionFailure, "WinRePartitionJoinUnavailable");
        var partitions = inventory.Partitions.Where(p => p.DiskNumber.Availability == ObservationAvailability.Available && p.DiskNumber.Value == disks[0].Number.Value &&
            p.Offset.Availability == ObservationAvailability.Available && p.Offset.Value == location.Value.PartitionOffset).ToArray();
        if (partitions.Length != 1) return Failure(partitions.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "WinRePartitionNotUnique");
        if (partitions[0].PartitionGuid.Availability != ObservationAvailability.Available) return Failure(partitions[0].PartitionGuid.Availability, "WinRePartitionGuidUnavailable");
        var volumeFailure = inventory.Volumes.Select(v => v.PartitionGuid.Availability)
            .FirstOrDefault(s => s != ObservationAvailability.Available, ObservationAvailability.Available);
        if (volumeFailure != ObservationAvailability.Available)
            return BindConfiguredAccessPath(storage, partitions[0], volumeFailure);
        var volumes = inventory.Volumes.Where(v => v.PartitionGuid.Availability == ObservationAvailability.Available && v.PartitionGuid.Value == partitions[0].PartitionGuid.Value).ToArray();
        if (volumes.Length != 1) return Failure(volumes.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "WinReVolumeNotUnique");
        return volumes[0].VolumeGuid.Availability == ObservationAvailability.Available ? CanonicalRecoveryIdentity.Bind(storage, volumes[0].VolumeGuid.Value) :
            Failure(volumes[0].VolumeGuid.Availability, "WinReVolumeGuidUnavailable");
    }

    // MSFT_Partition.AccessPaths names this partition's volume directly. Unknown ownership
    // on a different, fully observed volume GUID is not a dependency of that named volume.
    // Incomplete/duplicate paths or volume IDs cannot establish this exclusion.
    private static Observation<CanonicalVolumeIdentityV1> BindConfiguredAccessPath(
        Observation<WindowsStorageSnapshot> storage, PartitionObservation partition, ObservationAvailability unresolvedOwnerState)
    {
        var inventory = storage.Value;
        foreach (var row in inventory.Partitions)
            if (row.AccessPaths.Availability != ObservationAvailability.Available)
                return Failure(row.AccessPaths.Availability, "WinReAccessPathsUnavailable");
            else if (row.AccessPaths.Value.IsDefault)
                return Failure(ObservationAvailability.Ambiguous, "WinReAccessPathsMalformed");
            else if (row.AccessPaths.Value.Any(path => path is null ||
                path.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase) && !WindowsVolumeIdentity.TryParse(path, out _)))
                return Failure(ObservationAvailability.Ambiguous, "WinReAccessPathsMalformed");
        var ids = partition.AccessPaths.Value.Select(path => WindowsVolumeIdentity.TryParse(path, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length != 1) return Failure(ids.Length == 0 ? unresolvedOwnerState : ObservationAvailability.Ambiguous, "WinReAccessPathNotUnique");
        var selected = ids[0];
        if (inventory.Partitions.Count(p => p.AccessPaths.Value.Any(path => WindowsVolumeIdentity.TryParse(path, out var id) && id == selected)) != 1)
            return Failure(ObservationAvailability.Ambiguous, "WinReAccessPathMultipleOwners");
        foreach (var volume in inventory.Volumes)
        {
            if (volume.VolumeGuid.Availability != ObservationAvailability.Available)
                return Failure(volume.VolumeGuid.Availability, "WinReVolumeIdentityUnavailable");
            if (volume.VolumeGuid.Value == Guid.Empty)
                return Failure(ObservationAvailability.Ambiguous, "WinReVolumeIdentityMalformed");
        }
        if (inventory.Volumes.GroupBy(v => v.VolumeGuid.Value).Any(g => g.Count() != 1))
            return Failure(ObservationAvailability.Ambiguous, "WinReDuplicateVolumeIdentity");
        var candidates = inventory.Volumes.Where(v => v.VolumeGuid.Value == selected).ToArray();
        if (candidates.Length != 1) return Failure(ObservationAvailability.Unavailable, "WinReConfiguredVolumeMissing");
        if (candidates[0].PartitionGuid.Availability != ObservationAvailability.Available)
            return Failure(candidates[0].PartitionGuid.Availability, "WinReSelectedOwnerUnavailable");
        if (candidates[0].PartitionGuid.Value != partition.PartitionGuid.Value)
            return Failure(ObservationAvailability.Ambiguous, "WinReAccessPathOwnerMismatch");
        return CanonicalRecoveryIdentity.Bind(storage, selected);
    }

    private static Observation<ImmutableArray<byte>> ReadXml()
    {
        if (!OperatingSystem.IsWindows()) return Observations.Failure<ImmutableArray<byte>>(ObservationAvailability.Unsupported, "WindowsRequired");
        try
        {
            var systemDirectory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), systemDirectory, "Recovery", "ReAgent.xml");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > WinReConfigurationParser.MaximumBytes)
                return Observations.Failure<ImmutableArray<byte>>(ObservationAvailability.Ambiguous, "WinReXmlSize");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return Observations.Available(bytes.ToImmutableArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return Observations.Failure<ImmutableArray<byte>>(error is FileNotFoundException or DirectoryNotFoundException ? ObservationAvailability.Absent :
                ObservationErrors.Classify(error), "WinReXmlReadFailed");
        }
    }

    private static Observation<CanonicalVolumeIdentityV1> Failure(ObservationAvailability state, string code) => Observations.Failure<CanonicalVolumeIdentityV1>(state, code);
}
