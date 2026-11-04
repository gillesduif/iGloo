using System.Buffers.Binary;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedFatUuidReaderTests
{
    private static readonly Guid DiskGuid = Guid.Parse("1fa71e2d-9bb4-4d46-a999-8ebc26fef011");
    private static readonly Guid PartitionGuid = Guid.Parse("54f7618d-e0ef-4832-9963-28bb068bbac9");
    private static readonly Guid VolumeGuid = Guid.Parse("6358ef12-bac7-4668-a6ba-909741c0bf5b");

    [Fact]
    public void ExactVolumeGuidAndIndependentFatBytesMustAgree()
    {
        var volume = Volume();
        var root = $@"\\?\Volume{{{VolumeGuid:D}}}\";
        var result = ReviewedFatUuidReader.Read(volume,
            path =>
            {
                Assert.Equal(root, path);
                return ("FAT32", 0x1234ABCDu);
            },
            path =>
            {
                Assert.Equal(root[..^1], path);
                return Sector(0x1234ABCDu);
            });
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        Assert.Equal("1234-ABCD", result.Value);
    }

    [Fact]
    public void ChangedApiSerialOrBootBytesFailClosed()
    {
        var volume = Volume();
        Assert.Equal("ReviewedFatSerialReadbackChanged", ReviewedFatUuidReader.Read(volume,
            _ => ("FAT32", 0x1234ABCEu), _ => Sector(0x1234ABCDu)).Code);
        Assert.Equal("ReviewedFatSerialReadbackChanged", ReviewedFatUuidReader.Read(volume,
            _ => ("NTFS", 0x1234ABCDu), _ => Sector(0x1234ABCDu)).Code);
        var badBoot = Sector(0x1234ABCDu);
        badBoot[510] = 0;
        Assert.Equal("ReviewedFatBootSectorInvalid", ReviewedFatUuidReader.Read(volume,
            _ => ("FAT32", 0x1234ABCDu), _ => badBoot).Code);
        var wrongSectorSize = Sector(0x1234ABCDu);
        BinaryPrimitives.WriteUInt16LittleEndian(wrongSectorSize.AsSpan(11), 4096);
        Assert.Equal("ReviewedFatBootSectorInvalid", ReviewedFatUuidReader.Read(volume,
            _ => ("FAT32", 0x1234ABCDu), _ => wrongSectorSize).Code);
        var wrongFatGeometry = Sector(0x1234ABCDu);
        BinaryPrimitives.WriteUInt16LittleEndian(wrongFatGeometry.AsSpan(17), 16);
        Assert.Equal("ReviewedFatBootSectorInvalid", ReviewedFatUuidReader.Read(volume,
            _ => ("FAT32", 0x1234ABCDu), _ => wrongFatGeometry).Code);
        Assert.Equal(ObservationAvailability.Unavailable, ReviewedFatUuidReader.Read(volume,
            _ => throw new IOException("fixture"), _ => Sector(0x1234ABCDu)).Availability);
    }

    [Fact]
    public void FatReadRequiresTheCompleteLogicalSector()
    {
        var sector = Sector(0x1234ABCDu, 4096);
        Assert.Equal(0x1234ABCDu, ReviewedFatUuidReader.ParseSerial(sector, 4096));
        Assert.Throws<InvalidDataException>(() => ReviewedFatUuidReader.ParseSerial(
            sector.AsSpan(0, 512), 4096));
    }

    [Fact]
    public void UnboundVolumeIsRejectedBeforeAnyNativeRead()
    {
        var called = false;
        var changed = Volume() with { FileSystem = "NTFS" };
        var result = ReviewedFatUuidReader.Read(changed,
            _ => { called = true; return ("FAT32", 0x1234ABCDu); },
            _ => { called = true; return Sector(0x1234ABCDu); });
        Assert.Equal("ReviewedFatVolumeIdentityInvalid", result.Code);
        Assert.False(called);
    }

    private static CanonicalVolumeIdentityV1 Volume()
    {
        var disk = new CanonicalDiskIdentityV1("eui.fixture", 8, 17, DiskGuid,
            40UL * 1024 * 1024 * 1024, 512, 4096);
        return new(disk, PartitionGuid, VolumeGuid, PreparationSpacePlanning.EspType,
            1024 * 1024, 1024 * 1024 * 1024, "FAT32");
    }

    private static byte[] Sector(uint serial, ushort bytesPerSector = 512)
    {
        var bytes = new byte[bytesPerSector];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(11), bytesPerSector);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 1024);
        bytes[66] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(67), serial);
        "FAT32   "u8.CopyTo(bytes.AsSpan(82));
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        return bytes;
    }
}
