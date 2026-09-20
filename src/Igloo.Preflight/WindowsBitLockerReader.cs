using Igloo.Core.Abstractions;
using System.Management;

namespace Igloo.Preflight;

public sealed class WindowsBitLockerReader : IWindowsBitLockerReader
{
    public Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        try { return ReadExactVolumeCore(volume); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return Observations.Failure<BitLockerVolumeObservation>(WindowsStorageReader.ClassifyError(error), "BitLockerProviderUnavailable"); }
    }

    private static Observation<BitLockerVolumeObservation> ReadExactVolumeCore(VolumeObservation volume)
    {
        if (volume.VolumeGuid.Availability != ObservationAvailability.Available || volume.PartitionGuid.Availability != ObservationAvailability.Available ||
            volume.VolumeGuid.Value == Guid.Empty || volume.PartitionGuid.Value == Guid.Empty)
            return Observations.Failure<BitLockerVolumeObservation>(ObservationAvailability.Unavailable, "ExactVolumeRequired");
        var rows = WindowsStorageReader.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
            "SELECT * FROM Win32_EncryptableVolume");
        var selected = CorrelateVolume(rows, volume.VolumeGuid.Value);
        if (selected.Availability != ObservationAvailability.Available)
            return Observations.Failure<BitLockerVolumeObservation>(selected.Availability, selected.Code!);
        var match = selected.Value;
        if (string.IsNullOrEmpty(match.ObjectPath))
            return Observations.Failure<BitLockerVolumeObservation>(ObservationAvailability.Unavailable, "BitLockerObjectPathMissing");
        try
        {
            using var target = new ManagementObject(match.ObjectPath);
            return Observations.Available<BitLockerVolumeObservation>(new(volume.VolumeGuid.Value, StorageObservationProjection.Letter(match),
                ReadStatus(target, "GetConversionStatus", "ConversionStatus"), ReadStatus(target, "GetProtectionStatus", "ProtectionStatus"),
                ReadStatus(target, "GetLockStatus", "LockStatus"), StorageObservationProjection.Number(match, "EncryptionMethod")));
        }
        catch (Exception error) when (WindowsStorageReader.IsObservationError(error))
        { return Observations.Failure<BitLockerVolumeObservation>(WindowsStorageReader.ClassifyError(error), "BitLockerReadFailed"); }
    }

    internal static Observation<WindowsStorageRow> CorrelateVolume(WindowsStorageBatch rows, Guid volumeId)
    {
        if (rows.Availability != ObservationAvailability.Available)
            return Observations.Failure<WindowsStorageRow>(rows.Availability, "BitLockerProviderUnavailable");
        var matches = rows.Rows.Where(row => row.Properties.TryGetValue("DeviceID", out var value) &&
            WindowsVolumeIdentity.TryParse(value as string, out var id) && id == volumeId).ToArray();
        return matches.Length == 1 ? Observations.Available(matches[0]) : Observations.Failure<WindowsStorageRow>(
            matches.Length > 1 ? ObservationAvailability.Ambiguous : ObservationAvailability.Unavailable, "BitLockerVolumeNotUnique");
    }

    private static Observation<uint> ReadStatus(ManagementObject target, string method, string property)
    {
        try
        {
            using var result = target.InvokeMethod(method, null, null);
            if (result is null) return Observations.Failure<uint>(ObservationAvailability.Unavailable, "BitLockerMethodNoResult");
            if (result["ReturnValue"] is null) return Observations.Failure<uint>(ObservationAvailability.Unavailable, "BitLockerReturnMissing");
            var code = Convert.ToUInt32(result["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            if (code != 0) return Observations.Failure<uint>(code is 5 or 0x80070005 ? ObservationAvailability.AccessDenied :
                ObservationAvailability.Unavailable, "BitLockerMethodFailed");
            if (result[property] is null) return Observations.Failure<uint>(ObservationAvailability.Unavailable, "BitLockerFieldMissing");
            return Observations.Available<uint>(Convert.ToUInt32(result[property], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (WindowsStorageReader.IsObservationError(error))
        { return Observations.Failure<uint>(WindowsStorageReader.ClassifyError(error), "BitLockerMethodUnavailable"); }
    }

    public WindowsStorageBatch ReadByDriveLetter(char driveLetter)
    {
        if (!char.IsAsciiLetter(driveLetter)) return new([], new ManagementException("Invalid drive-letter observation."));
        return WindowsStorageReader.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
            $"SELECT DeviceID, DriveLetter, ConversionStatus, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}:'");
    }

    // Compatibility interpretation only. This does not observe lock status or prove volume binding.
    internal static BitLockerState Project(uint? conversion, uint? protection)
    {
        if (conversion is null || protection is null) return BitLockerState.Unknown;
        if (conversion == 0) return BitLockerState.NotEncrypted;
        if (conversion == 3 || conversion == 5) return BitLockerState.DecryptionInProgress;
        if (protection == 1) return BitLockerState.EncryptedAndUnlocked;
        if (protection == 0) return BitLockerState.SuspendedProtection;
        return BitLockerState.Unknown;
    }
}
