namespace Igloo.Core.Abstractions;

public interface IWindowsBitLockerReader
{
    WindowsStorageBatch ReadByDriveLetter(char driveLetter);
}
