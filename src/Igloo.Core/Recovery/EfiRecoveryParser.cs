using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Recovery;

/// <summary>
/// Pure, bounded parser for UEFI load options. Supports identity only for the proven single GPT HD/FilePath/End shape.
/// See UEFI 2.10 sections 3.1.3 and 10.3.5. Unknown shapes retain raw evidence and never gain a guessed identity.
/// </summary>
public static class EfiRecoveryParser
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    public static FirmwareVariableV1 Observe(FirmwareVariableObservation raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new(raw.Availability == ObservationAvailability.Available
            ? Observations.Available(raw.Data!.Value)
            : Observations.Failure<ImmutableArray<byte>>(raw.Availability, "FirmwareRead"), raw.NativeError,
            Observations.Failure<uint>(ObservationAvailability.Unsupported, "PropertyNotExposed"));
    }

    public static Observation<ImmutableArray<ushort>> ParseBootOrder(FirmwareVariableV1 raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Bytes.Availability != ObservationAvailability.Available)
            return Observations.Failure<ImmutableArray<ushort>>(raw.Bytes.Availability, raw.Bytes.Code ?? "FirmwareRead");
        var bytes = raw.Bytes.Value;
        if (bytes.IsDefaultOrEmpty || bytes.Length % 2 != 0)
            return Observations.Failure<ImmutableArray<ushort>>(ObservationAvailability.Ambiguous, "MalformedBootOrder");
        var order = ImmutableArray.CreateBuilder<ushort>(bytes.Length / 2);
        for (var i = 0; i < bytes.Length; i += 2)
            order.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i, 2)));
        // Duplicate entries do not have an independently justified boot-selection interpretation.
        if (order.Distinct().Count() != order.Count)
            return Observations.Failure<ImmutableArray<ushort>>(ObservationAvailability.Ambiguous, "DuplicateBootOrderIndex");
        return Observations.Available(order.MoveToImmutable());
    }

    public static Observation<ushort> ParseBootNext(FirmwareVariableV1 raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Bytes.Availability != ObservationAvailability.Available)
            return Observations.Failure<ushort>(raw.Bytes.Availability, raw.Bytes.Code ?? "FirmwareRead");
        var bytes = raw.Bytes.Value;
        return !bytes.IsDefault && bytes.Length == 2
            ? Observations.Available(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan()))
            : Observations.Failure<ushort>(ObservationAvailability.Ambiguous, "MalformedBootNext");
    }

    public static EfiBootEntryV1 ParseBootEntry(ushort index, FirmwareVariableV1 raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new(index, raw, raw.Bytes.Availability == ObservationAvailability.Available
            ? ParseLoadOption(raw.Bytes.Value)
            : Observations.Failure<EfiLoadOptionV1>(raw.Bytes.Availability, raw.Bytes.Code ?? "FirmwareRead"));
    }

    public static Observation<EfiLoadOptionV1> ParseLoadOption(ImmutableArray<byte> raw)
    {
        if (raw.IsDefaultOrEmpty || raw.Length < 8) return Invalid("TruncatedLoadOption");
        var span = raw.AsSpan();
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var descriptionEnd = -1;
        for (var i = 6; i + 1 < raw.Length; i += 2)
            if (span[i] == 0 && span[i + 1] == 0) { descriptionEnd = i; break; }
        if (descriptionEnd < 0) return Invalid("UnterminatedDescription");
        string description;
        try { description = StrictUnicode.GetString(span[6..descriptionEnd]); }
        catch (DecoderFallbackException) { return Invalid("InvalidDescriptionEncoding"); }
        var pathStart = descriptionEnd + 2;
        if (pathLength > raw.Length - pathStart) return Invalid("TruncatedFilePathList");
        var pathRaw = ImmutableArray.Create(span.Slice(pathStart, pathLength).ToArray());
        var nodes = ImmutableArray.CreateBuilder<EfiDevicePathNodeV1>();
        var paths = pathRaw.AsSpan();
        var offset = 0;
        while (offset < paths.Length)
        {
            if (paths.Length - offset < 4) return Invalid("TruncatedDevicePathHeader");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(paths[(offset + 2)..]);
            if (length < 4 || length > paths.Length - offset) return Invalid("InvalidDevicePathLength");
            if (paths[offset] == 0x7f && length != 4) return Invalid("InvalidEndNodeLength");
            nodes.Add(new(paths[offset], paths[offset + 1], ImmutableArray.Create(paths.Slice(offset, length).ToArray())));
            offset += length;
        }
        if (nodes.Count == 0 || nodes[^1].Type != 0x7f || nodes[^1].SubType != 0xff)
            return Invalid("MissingEndEntireDevicePath");
        var immutableNodes = nodes.ToImmutable();
        return Observations.Available(new EfiLoadOptionV1(attributes, description, pathRaw, immutableNodes,
            ImmutableArray.Create(span[(pathStart + pathLength)..].ToArray()), ParseGptFilePath(immutableNodes)));
    }

    private static Observation<EfiGptFilePathV1> ParseGptFilePath(ImmutableArray<EfiDevicePathNodeV1> nodes)
    {
        if (nodes.Length != 3 || nodes[0].Type != 4 || nodes[0].SubType != 1 ||
            nodes[1].Type != 4 || nodes[1].SubType != 4 || nodes[2].Type != 0x7f || nodes[2].SubType != 0xff)
            return UnsupportedPath("UnsupportedDevicePathShape");
        var hd = nodes[0].Raw.AsSpan();
        if (hd.Length != 42) return AmbiguousPath("MalformedHardDriveNode");
        if (hd[40] != 2 || hd[41] != 2) return UnsupportedPath("NonGptHardDriveNode");
        var partition = BinaryPrimitives.ReadUInt32LittleEndian(hd[4..]);
        var start = BinaryPrimitives.ReadUInt64LittleEndian(hd[8..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(hd[16..]);
        var guid = new Guid(hd.Slice(24, 16));
        if (partition == 0 || start == 0 || size == 0 || guid == Guid.Empty || start > ulong.MaxValue - size)
            return AmbiguousPath("InvalidGptPartitionIdentity");
        var pathBytes = nodes[1].Raw.AsSpan()[4..];
        if (pathBytes.Length < 4 || pathBytes.Length % 2 != 0 || pathBytes[^2] != 0 || pathBytes[^1] != 0)
            return AmbiguousPath("MalformedFilePath");
        string path;
        try { path = StrictUnicode.GetString(pathBytes[..^2]); }
        catch (DecoderFallbackException) { return AmbiguousPath("InvalidFilePathEncoding"); }
        if (path.Contains('\0', StringComparison.Ordinal) || !path.StartsWith('\\') || path.Contains('/', StringComparison.Ordinal) ||
            path.Split('\\').Any(component => component is "." or ".."))
            return AmbiguousPath("NonCanonicalEfiFilePath");
        return Observations.Available(new EfiGptFilePathV1(guid, start, size, partition, path));
    }

    private static Observation<EfiLoadOptionV1> Invalid(string code) =>
        Observations.Failure<EfiLoadOptionV1>(ObservationAvailability.Ambiguous, code);
    private static Observation<EfiGptFilePathV1> UnsupportedPath(string code) =>
        Observations.Failure<EfiGptFilePathV1>(ObservationAvailability.Unsupported, code);
    private static Observation<EfiGptFilePathV1> AmbiguousPath(string code) =>
        Observations.Failure<EfiGptFilePathV1>(ObservationAvailability.Ambiguous, code);
}
