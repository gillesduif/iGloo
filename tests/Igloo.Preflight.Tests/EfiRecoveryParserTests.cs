using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class EfiRecoveryParserTests
{
    private static readonly Guid EspGuid = new("98e44c1d-62fb-4b8b-9dd7-f0ab22f721fa");

    [Fact]
    public void WindowsGptPathHasStructuralIdentityAndLosslessBytes()
    {
        var raw = WindowsEntry([0x00, 0xff, 0x37, 0x90]);
        var entry = EfiRecoveryParser.ParseBootEntry(0x1234, Observed(raw));
        Assert.Equal(raw, entry.Raw.Bytes.Value);
        Assert.Equal((ushort)0x1234, entry.Index);
        var option = entry.LoadOption.Value;
        Assert.Equal(1U, option.Attributes);
        Assert.Equal("Windows Boot Manager", option.Description);
        Assert.Equal(new byte[] { 0, 255, 55, 144 }, option.OptionalData);
        Assert.Equal(3, option.DevicePathNodes.Length);
        var path = option.GptFilePath.Value;
        Assert.Equal(EspGuid, path.PartitionGuid);
        Assert.Equal(2048UL, path.StartLba);
        Assert.Equal(204800UL, path.SizeLba);
        Assert.Equal(1U, path.ObservedPartitionNumber);
        Assert.Equal(@"\EFI\Microsoft\Boot\bootmgfw.efi", path.FilePath);
    }

    [Fact]
    public void GuidInsideUnknownNodeOrOptionalDataDoesNotInventIdentity()
    {
        var raw = LoadOption([.. Node(1, 1, EspGuid.ToByteArray()), .. End()], EspGuid.ToByteArray());
        var entry = EfiRecoveryParser.ParseBootEntry(7, Observed(raw));
        Assert.Equal(raw, entry.Raw.Bytes.Value);
        Assert.Equal(ObservationAvailability.Available, entry.LoadOption.Availability);
        Assert.Equal(ObservationAvailability.Unsupported, entry.LoadOption.Value.GptFilePath.Availability);
        Assert.Equal(EspGuid.ToByteArray(), entry.LoadOption.Value.OptionalData);
        Assert.Throws<InvalidOperationException>(() => entry.LoadOption.Value.GptFilePath.Value);
    }

    [Fact]
    public void MultiplePathInstancesAreRetainedWithoutInventedSingleIdentity()
    {
        var raw = LoadOption([.. HardDrive(), .. FilePath(@"\EFI\BOOT\BOOTX64.EFI"), 0x7f, 1, 4, 0,
            .. HardDrive(), .. FilePath(@"\EFI\Microsoft\Boot\bootmgfw.efi"), .. End()], []);
        var entry = EfiRecoveryParser.ParseBootEntry(2, Observed(raw));
        Assert.Equal(raw, entry.Raw.Bytes.Value);
        Assert.Equal(6, entry.LoadOption.Value.DevicePathNodes.Length);
        Assert.Equal(ObservationAvailability.Unsupported, entry.LoadOption.Value.GptFilePath.Availability);
    }

    [Fact]
    public void MbrNodeWithGuidShapedSignatureIsStillUnsupported()
    {
        var hd = HardDrive();
        hd[40] = 1;
        hd[41] = 1;
        var entry = EfiRecoveryParser.ParseLoadOption(LoadOption([.. hd, .. FilePath(@"\EFI\BOOT\BOOTX64.EFI"), .. End()], []));
        Assert.Equal(ObservationAvailability.Unsupported, entry.Value.GptFilePath.Availability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MalformedLoadOptionBoundsStayAmbiguousAndRawIsRetained(int mutation)
    {
        byte[] bytes = mutation switch
        {
            0 => [1, 0, 0],
            1 => [1, 0, 0, 0, 0, 0, 65, 0], // No description terminator.
            2 => [1, 0, 0, 0, 255, 255, 0, 0], // Path beyond end.
            3 => LoadOption([4, 1, 3, 0, .. End()], []).ToArray(), // Node shorter than header.
            4 => LoadOption([4, 1, 255, 255, .. End()], []).ToArray(), // Node beyond list.
            _ => LoadOption(HardDrive(), []).ToArray(), // Missing end node.
        };
        var raw = ImmutableArray.Create(bytes);
        var result = EfiRecoveryParser.ParseBootEntry(0, Observed(raw));
        Assert.Equal(ObservationAvailability.Ambiguous, result.LoadOption.Availability);
        Assert.Equal(raw, result.Raw.Bytes.Value);
    }

    [Theory]
    [InlineData(@"EFI\Microsoft\Boot\bootmgfw.efi")]
    [InlineData(@"\EFI\..\bootmgfw.efi")]
    [InlineData(@"\EFI/bootmgfw.efi")]
    public void NonCanonicalPathsAreAmbiguous(string path)
    {
        var result = EfiRecoveryParser.ParseLoadOption(LoadOption([.. HardDrive(), .. FilePath(path), .. End()], []));
        Assert.Equal(ObservationAvailability.Ambiguous, result.Value.GptFilePath.Availability);
    }

    [Fact]
    public void MalformedUtf16PathDoesNotAcquireIdentity()
    {
        var path = Node(4, 4, [0, 0xd8, 0, 0]); // Unpaired high surrogate.
        var result = EfiRecoveryParser.ParseLoadOption(LoadOption([.. HardDrive(), .. path, .. End()], []));
        Assert.Equal(ObservationAvailability.Ambiguous, result.Value.GptFilePath.Availability);
    }

    [Fact]
    public void BootOrderRetainsOrderAndUsesLittleEndian()
    {
        var result = EfiRecoveryParser.ParseBootOrder(Observed([0, 2, 1, 0, 0xff, 0xab]));
        Assert.Equal(new ushort[] { 0x200, 1, 0xabff }, result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void EmptyOrOddBootOrderIsAmbiguous(int length) =>
        Assert.Equal(ObservationAvailability.Ambiguous,
            EfiRecoveryParser.ParseBootOrder(Observed(ImmutableArray.Create(new byte[length]))).Availability);

    [Fact]
    public void DuplicateBootOrderDoesNotClaimExactness() => Assert.Equal(ObservationAvailability.Ambiguous,
        EfiRecoveryParser.ParseBootOrder(Observed([1, 0, 1, 0])).Availability);

    [Theory]
    [InlineData(203, ObservationAvailability.Absent)]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(1300, ObservationAvailability.AccessDenied)]
    [InlineData(1314, ObservationAvailability.AccessDenied)]
    [InlineData(1, ObservationAvailability.Unsupported)]
    [InlineData(50, ObservationAvailability.Unsupported)]
    [InlineData(122, ObservationAvailability.Unavailable)]
    [InlineData(0, ObservationAvailability.Unavailable)]
    public void NativeFailuresPreserveClassificationAndNativeError(int error, ObservationAvailability expected)
    {
        var raw = EfiRecoveryParser.Observe(new(null, error));
        Assert.Equal(error, raw.NativeError);
        Assert.Equal(expected, raw.Bytes.Availability);
        Assert.Equal(expected, EfiRecoveryParser.ParseBootNext(raw).Availability);
        Assert.Equal(expected, EfiRecoveryParser.ParseBootOrder(raw).Availability);
        Assert.Equal(expected, EfiRecoveryParser.ParseBootEntry(0, raw).LoadOption.Availability);
    }

    [Fact]
    public void BootNextIsExactlyOneLittleEndianUshortAndAbsentIsDifferent()
    {
        Assert.Equal((ushort)0xab12, EfiRecoveryParser.ParseBootNext(Observed([0x12, 0xab])).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, EfiRecoveryParser.ParseBootNext(Observed([])).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, EfiRecoveryParser.ParseBootNext(Observed([1])).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, EfiRecoveryParser.ParseBootNext(Observed([1, 0, 0])).Availability);
        Assert.Equal(ObservationAvailability.Absent, EfiRecoveryParser.ParseBootNext(EfiRecoveryParser.Observe(new(null, 203))).Availability);
    }

    [Fact]
    public void CaptureReadsOnlyExplicitAndReferencedEntriesOnceAndRetainsRequiredScope()
    {
        var reader = new FirmwareFake();
        var result = new WindowsFirmwareSnapshotCapture(reader).Capture([7, 7, 0x200]);
        Assert.Equal(new ushort[] { 7, 0x200 }, result.RequiredBootEntries);
        Assert.Equal(new ushort[] { 1, 7, 9, 0x200 }, result.BootEntries.Select(entry => entry.Index));
        Assert.Equal(new ushort[] { 1, 7, 9, 0x200 }, reader.Reads);
        Assert.Equal(ObservationAvailability.Unsupported, result.BootOrderRaw.VariableAttributes.Availability);
        Assert.Equal("PropertyNotExposed", result.BootOrderRaw.VariableAttributes.Code);
    }

    [Theory]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(50, ObservationAvailability.Unsupported)]
    [InlineData(203, ObservationAvailability.Unavailable)]
    public void ReaderExceptionNeverBecomesAbsence(int error, ObservationAvailability expected)
    {
        var result = new WindowsFirmwareSnapshotCapture(new FirmwareFake { ThrowError = error }).Capture([]);
        Assert.Equal(expected, result.BootOrder.Availability);
        Assert.Equal(error, result.BootOrderRaw.NativeError);
        Assert.Equal(ObservationAvailability.Available, result.BootNext.Availability);
    }

    private static FirmwareVariableV1 Observed(ImmutableArray<byte> bytes) => EfiRecoveryParser.Observe(new(bytes, 0));
    private static ImmutableArray<byte> WindowsEntry(byte[] optionalData) =>
        LoadOption([.. HardDrive(), .. FilePath(@"\EFI\Microsoft\Boot\bootmgfw.efi"), .. End()], optionalData);

    private static ImmutableArray<byte> LoadOption(byte[] path, byte[] optionalData)
    {
        var description = Encoding.Unicode.GetBytes("Windows Boot Manager\0");
        var bytes = new byte[6 + description.Length + path.Length + optionalData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)path.Length));
        description.CopyTo(bytes, 6);
        path.CopyTo(bytes, 6 + description.Length);
        optionalData.CopyTo(bytes, 6 + description.Length + path.Length);
        return ImmutableArray.Create(bytes);
    }

    private static byte[] HardDrive()
    {
        var bytes = new byte[42];
        bytes[0] = 4;
        bytes[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 2048);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 204800);
        EspGuid.TryWriteBytes(bytes.AsSpan(24));
        bytes[40] = 2;
        bytes[41] = 2;
        return bytes;
    }

    private static byte[] FilePath(string path) => Node(4, 4, Encoding.Unicode.GetBytes(path + '\0'));
    private static byte[] End() => [0x7f, 0xff, 4, 0];
    private static byte[] Node(byte type, byte subType, byte[] payload)
    {
        var bytes = new byte[4 + payload.Length];
        bytes[0] = type;
        bytes[1] = subType;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)bytes.Length));
        payload.CopyTo(bytes, 4);
        return bytes;
    }

    private sealed class FirmwareFake : IWindowsFirmwareReader
    {
        public int? ThrowError { get; init; }
        public List<ushort> Reads { get; } = [];
        public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => ThrowError.HasValue
            ? throw new Win32Exception(ThrowError.Value) : new([0, 2, 1, 0], 0);
        public FirmwareVariableObservation ReadBootNext(int bufferBytes = 4096) => new([9, 0], 0);
        public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096)
        {
            Reads.Add(index);
            return new(WindowsEntry([]), 0);
        }
    }
}
