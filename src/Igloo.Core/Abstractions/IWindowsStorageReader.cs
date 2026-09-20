namespace Igloo.Core.Abstractions;

public interface IWindowsStorageReader
{
    WindowsStorageBatch ReadDisks();
    WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false);
    WindowsStorageBatch ReadVolumes(char driveLetter);
    WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false);
}
