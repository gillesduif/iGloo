using Igloo.Core.Abstractions;
using System.Management;

namespace Igloo.Preflight;

public sealed class WindowsBitLockerReader : IWindowsBitLockerReader
{
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
