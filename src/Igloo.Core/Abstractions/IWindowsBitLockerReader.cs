namespace Igloo.Core.Abstractions;

public interface IWindowsBitLockerReader
{
    Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume) =>
        Observations.Failure<BitLockerVolumeObservation>(ObservationAvailability.Unsupported, "ExactVolumeNotImplemented");
    WindowsStorageBatch ReadByDriveLetter(char driveLetter);
}
