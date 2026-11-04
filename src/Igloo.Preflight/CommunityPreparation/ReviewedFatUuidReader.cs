using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityPreparation;

// FAT's Linux UUID is the on-disk 32-bit volume serial. Read it from the
// exact Windows Volume GUID and require the independent filesystem API and
// FAT32 boot-sector bytes to agree. The caller brackets this with fresh GPT
// and canonical-volume observations; this class alone grants no ownership.
[SupportedOSPlatform("windows")]
internal static class ReviewedFatUuidReader
{
    internal static Observation<string> Read(CanonicalVolumeIdentityV1 volume,
        Func<string, (string FileSystem, uint Serial)>? readApi = null,
        Func<string, byte[]>? readSector = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (!CanonicalRecoveryIdentity.IsValid(volume) || volume.FileSystem != "FAT32" ||
            volume.PartitionType != PreparationSpacePlanning.EspType &&
            volume.PartitionType != PreparationSpacePlanning.BasicDataType)
            return Observations.Failure<string>(ObservationAvailability.Ambiguous,
                "ReviewedFatVolumeIdentityInvalid");
        var root = $@"\\?\Volume{{{volume.VolumeGuid:D}}}\";
        var device = root[..^1];
        try
        {
            var api = (readApi ?? ReadApi)(root);
            var sector = readSector is null ? ReadSector(device,
                volume.Disk.LogicalSectorSize) : readSector(device);
            var rawSerial = ParseSerial(sector, volume.Disk.LogicalSectorSize);
            if (!string.Equals(api.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) ||
                api.Serial == 0 || api.Serial != rawSerial)
                return Observations.Failure<string>(ObservationAvailability.Ambiguous,
                    "ReviewedFatSerialReadbackChanged");
            var hex = api.Serial.ToString("X8", CultureInfo.InvariantCulture);
            return Observations.Available(hex[..4] + "-" + hex[4..]);
        }
        catch (InvalidDataException)
        {
            return Observations.Failure<string>(ObservationAvailability.Ambiguous,
                "ReviewedFatBootSectorInvalid");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            Win32Exception or NotSupportedException)
        {
            return Observations.Failure<string>(ObservationAvailability.Unavailable,
                "ReviewedFatReadbackUnavailable");
        }
    }

    internal static uint ParseSerial(ReadOnlySpan<byte> sector, uint expectedBytesPerSector)
    {
        if (expectedBytesPerSector is not (512 or 1024 or 2048 or 4096) ||
            sector.Length < expectedBytesPerSector ||
            BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]) != expectedBytesPerSector ||
            BinaryPrimitives.ReadUInt16LittleEndian(sector[17..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(sector[22..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(sector[36..]) == 0 ||
            sector[66] != 0x29 || sector[510] != 0x55 || sector[511] != 0xAA ||
            !sector.Slice(82, 8).SequenceEqual("FAT32   "u8))
            throw new InvalidDataException("The exact FAT32 boot sector is incomplete or changed.");
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(sector[67..]);
        if (serial == 0)
            throw new InvalidDataException("The FAT32 volume has no usable serial.");
        return serial;
    }

    private static (string FileSystem, uint Serial) ReadApi(string root)
    {
        var fileSystem = new char[32];
        if (!GetVolumeInformation(root, null, 0, out var serial, out _, out _,
                fileSystem, checked((uint)fileSystem.Length)))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The exact FAT volume information is unavailable.");
        var terminator = Array.IndexOf(fileSystem, '\0');
        if (terminator <= 0)
            throw new InvalidDataException("The exact FAT filesystem name is incomplete.");
        return (new string(fileSystem, 0, terminator), serial);
    }

    private static byte[] ReadSector(string device, uint logicalSectorSize)
    {
        using var handle = File.OpenHandle(device, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var sector = new byte[checked((int)logicalSectorSize)];
        if (RandomAccess.Read(handle, sector, 0) != sector.Length)
            throw new InvalidDataException("The exact FAT boot sector is incomplete.");
        return sector;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPath,
        [Out] char[]? volumeName, uint volumeNameSize, out uint serial,
        out uint maximumComponentLength, out uint fileSystemFlags,
        [Out] char[] fileSystemName, uint fileSystemNameSize);
}
