using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

// Native registry type and bytes are retained without string expansion, Unicode
// terminator repair, signed-number conversion, or a default numeric value.
public sealed record RegistryValueV1(uint NativeType, ImmutableArray<byte> RawData);

// This is Windows' RTC interpretation setting, not a sample of the hardware clock.
// Key absence differs from value absence and from a failed query.
public sealed record RtcRegistryStateV1(Observation<bool> KeyPresent,
    Observation<RegistryValueV1> RealTimeIsUniversal)
{
    public const int SchemaVersion = 1;
    public int SectionVersion { get; init; } = SchemaVersion;
    public const string Hive = "HKEY_LOCAL_MACHINE";
    public const string RegistryView = "Registry64";
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation";
    public const string ValueName = "RealTimeIsUniversal";
}
