using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityRecovery;

// Community orchestration of the shared readers, not a second Windows observation stack.
internal static class WindowsBootRegistrationPlanning
{
    public static Observation<CanonicalVolumeIdentityV1> ObservePreparedTarget(IWindowsStorageReader reader, uint disk, uint partition)
    {
        var storage = reader.ReadIdentitySnapshot();
        if (storage.Availability != ObservationAvailability.Available)
            return Observations.Failure<CanonicalVolumeIdentityV1>(storage.Availability, "PreparedTargetInventoryUnavailable");
        // Ordinals locate the result of Prepare within ONE inventory. Subsequent reads bind only
        // the pinned canonical identity; they never silently replace it with the same ordinal.
        var partitions = storage.Value.Partitions.Where(p => p.DiskNumber.Availability == ObservationAvailability.Available && p.DiskNumber.Value == disk &&
            p.Number.Availability == ObservationAvailability.Available && p.Number.Value == partition).ToArray();
        if (partitions.Length != 1)
            return Observations.Failure<CanonicalVolumeIdentityV1>(partitions.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "PreparedPartitionNotUnique");
        if (partitions[0].PartitionGuid.Availability != ObservationAvailability.Available)
            return Observations.Failure<CanonicalVolumeIdentityV1>(partitions[0].PartitionGuid.Availability, "PreparedPartitionIdentityUnavailable");
        var volumes = storage.Value.Volumes.Where(v => v.PartitionGuid.Availability == ObservationAvailability.Available &&
            v.PartitionGuid.Value == partitions[0].PartitionGuid.Value).ToArray();
        if (volumes.Length != 1)
            return Observations.Failure<CanonicalVolumeIdentityV1>(volumes.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "PreparedVolumeNotUnique");
        if (volumes[0].VolumeGuid.Availability != ObservationAvailability.Available)
            return Observations.Failure<CanonicalVolumeIdentityV1>(volumes[0].VolumeGuid.Availability, "PreparedVolumeIdentityUnavailable");
        return CanonicalRecoveryIdentity.Bind(storage, volumes[0].VolumeGuid.Value);
    }

    public static BootRegistrationEvidence Read(IWindowsStorageReader storageReader, IWindowsBcdReader bcdReader,
        CanonicalVolumeIdentityV1 preparedTarget, uint partitionNumber)
    {
        var storage = storageReader.ReadIdentitySnapshot();
        var target = CanonicalRecoveryIdentity.Bind(storage, preparedTarget.VolumeGuid);
        var windowsId = WindowsRecoveryPathReader.ReadWindowsVolume();
        var windows = windowsId.Availability == ObservationAvailability.Available ? CanonicalRecoveryIdentity.Bind(storage, windowsId.Value) :
            Observations.Failure<CanonicalVolumeIdentityV1>(windowsId.Availability, "BootPlanWindowsVolumeUnavailable");
        var binding = target.Availability != ObservationAvailability.Available
            ? Observations.Failure<RecoveryTargetBindingV1>(target.Availability, "BootPlanTargetUnavailable")
            : target.Value != preparedTarget ? Observations.Failure<RecoveryTargetBindingV1>(ObservationAvailability.Ambiguous, "BootPlanPreparedTargetChanged")
            : windows.Availability != ObservationAvailability.Available ? Observations.Failure<RecoveryTargetBindingV1>(windows.Availability, "BootPlanWindowsUnavailable")
            : Observations.Available(new RecoveryTargetBindingV1(windows.Value, target.Value));
        return new(binding, partitionNumber, bcdReader.ReadRecoveryGraph(),
            new WindowsFirmwareSnapshotCapture(WindowsFirmwareReader.Shared).Capture(Enumerable.Range(0, 256).Select(i => (ushort)i)),
            new WindowsRtcRecoveryReader().Capture());
    }
}
