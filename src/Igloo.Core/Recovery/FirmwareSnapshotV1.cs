using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

/// <summary>Read-only firmware evidence; native errors and failed observations never become empty state.</summary>
public sealed record FirmwareVariableV1(
    Observation<ImmutableArray<byte>> Bytes,
    int? NativeError,
    Observation<uint> VariableAttributes);

/// <summary>Raw bytes retain all load-option data, even when a path has no supported structural identity.</summary>
public sealed record EfiBootEntryV1(ushort Index, FirmwareVariableV1 Raw, Observation<EfiLoadOptionV1> LoadOption);

public sealed record EfiDevicePathNodeV1(byte Type, byte SubType, ImmutableArray<byte> Raw);

/// <summary>Partition number is captured device-path evidence, never canonical storage identity.</summary>
public sealed record EfiGptFilePathV1(
    Guid PartitionGuid, ulong StartLba, ulong SizeLba, uint ObservedPartitionNumber, string FilePath);

public sealed record EfiLoadOptionV1(
    uint Attributes,
    string Description,
    ImmutableArray<byte> FilePathListRaw,
    ImmutableArray<EfiDevicePathNodeV1> DevicePathNodes,
    ImmutableArray<byte> OptionalData,
    Observation<EfiGptFilePathV1> GptFilePath);

/// <summary>
/// BootOrder is ordered state. Entry collection order is not state. Required indices are explicit scope input;
/// entries referenced by BootOrder/BootNext may additionally be retained as observed evidence.
/// Native variable attributes are not exposed by the existing reader and are not inferred from load-option attributes.
/// </summary>
public sealed record FirmwareSnapshotV1(
    int SectionVersion,
    FirmwareVariableV1 BootOrderRaw,
    Observation<ImmutableArray<ushort>> BootOrder,
    FirmwareVariableV1 BootNextRaw,
    Observation<ushort> BootNext,
    ImmutableArray<ushort> RequiredBootEntries,
    ImmutableArray<EfiBootEntryV1> BootEntries);
