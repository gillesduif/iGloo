using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Igloo.Preflight.CommunityPreparation;

internal sealed record ReviewedGptPartitionV1(Guid PartitionGuid, Guid PartitionType,
    ulong OffsetBytes, ulong SizeBytes);
internal sealed record ReviewedGptUsableRangeV1(Guid DiskGuid, ulong FirstUsableOffsetBytes,
    ulong UsableLengthBytes, uint PartitionCount, ImmutableArray<ReviewedGptPartitionV1> Partitions);

// Windows parses the on-disk GPT for this read-only ioctl. A caller must still
// compare the GUID, usable bounds and every nonempty partition entry with a
// complete fresh WMI inventory; the disk ordinal by itself is never authority.
[SupportedOSPlatform("windows")]
internal static class ReviewedGptUsableRangeReader
{
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    private const int HeaderBytes = 48;
    // Windows' PARTITION_INFORMATION_EX has 32 bytes before its 112-byte GPT
    // union. The layout header includes an eight-byte aligned GPT union.
    private const int EntryBytes = 144;
    private const int OutputBytes = 65536;

    internal static ReviewedGptUsableRangeV1 Read(uint diskNumber)
    {
        using var handle = File.OpenHandle($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite);
        var output = new byte[OutputBytes];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveLayoutEx, IntPtr.Zero, 0,
                output, checked((uint)output.Length), out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The exact Windows GPT usable range is unavailable.");
        return Parse(output.AsSpan(0, checked((int)returned)));
    }

    internal static ReviewedGptUsableRangeV1 Parse(ReadOnlySpan<byte> layout)
    {
        if (layout.Length < HeaderBytes)
            throw new InvalidDataException("The Windows drive layout header is incomplete.");
        var header = layout[..HeaderBytes];
        var style = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var diskGuid = new Guid(header[8..24]);
        var first = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        var maxCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        if (style != 1 || diskGuid == Guid.Empty || count == 0 || maxCount < count ||
            first <= 0 || length <= 0)
            throw new InvalidDataException("The Windows drive layout is not a complete GPT range.");
        if (count > (layout.Length - HeaderBytes) / EntryBytes)
            throw new InvalidDataException("The Windows drive layout omits claimed partition entries.");
        var partitions = ImmutableArray.CreateBuilder<ReviewedGptPartitionV1>();
        var numbers = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var entry = layout.Slice(HeaderBytes + checked((int)index * EntryBytes), EntryBytes);
            var entryStyle = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var start = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            var number = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
            var type = new Guid(entry[32..48]);
            var id = new Guid(entry[48..64]);
            if (entryStyle != 1 || type == Guid.Empty || id == Guid.Empty ||
                start <= 0 || size <= 0 || number == 0)
            {
                // An unused table slot must contain no partition identity or
                // geometry. Any partly populated entry is ambiguous.
                if (entryStyle is not (0 or 1 or 2) || start != 0 || size != 0 ||
                    number != 0 || type != Guid.Empty || id != Guid.Empty)
                    throw new InvalidDataException("The Windows GPT has a partial partition entry.");
                continue;
            }
            if (!numbers.Add(number) || partitions.Any(p => p.PartitionGuid == id))
                throw new InvalidDataException("The Windows GPT has a duplicate partition entry.");
            partitions.Add(new(id, type, checked((ulong)start), checked((ulong)size)));
        }
        if (partitions.Count == 0)
            throw new InvalidDataException("The Windows GPT contains no observed partitions.");
        return new(diskGuid, checked((ulong)first), checked((ulong)length), count,
            partitions.ToImmutable());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, uint inputBytes, [Out] byte[] output, uint outputBytes,
        out uint bytesReturned, IntPtr overlapped);
}
