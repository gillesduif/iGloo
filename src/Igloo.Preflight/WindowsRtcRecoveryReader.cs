using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Microsoft.Win32.SafeHandles;

namespace Igloo.Preflight;

/// <summary>Reads only the fixed Windows RTC registry setting. Opens no writable handles.</summary>
public sealed partial class WindowsRtcRecoveryReader
{
    private const uint MaximumValueBytes = 1024 * 1024;
    private readonly Func<RtcRegistryStateV1> _read;

    public WindowsRtcRecoveryReader() : this(ReadOnce) { }
    internal WindowsRtcRecoveryReader(Func<RtcRegistryStateV1> read) => _read = read;

    // Reopens the key independently. Agreement is a bounded consistency check,
    // not an atomic system snapshot or permission for mutation.
    public RtcRegistryStateV1 Capture()
    {
        var first = _read();
        var second = _read();
        if (Equivalent(first, second)) return first;
        return Failure(ObservationAvailability.Ambiguous, "RtcObservationChangedDuringCapture");
    }

    private static bool Equivalent(RtcRegistryStateV1 first, RtcRegistryStateV1 second)
    {
        if (first.KeyPresent.Availability != second.KeyPresent.Availability ||
            first.RealTimeIsUniversal.Availability != second.RealTimeIsUniversal.Availability)
            return false;
        if (first.KeyPresent.Availability == ObservationAvailability.Available &&
            first.KeyPresent.Value != second.KeyPresent.Value) return false;
        if (first.RealTimeIsUniversal.Availability != ObservationAvailability.Available) return true;
        return first.RealTimeIsUniversal.Value.NativeType == second.RealTimeIsUniversal.Value.NativeType &&
            first.RealTimeIsUniversal.Value.RawData.AsSpan().SequenceEqual(second.RealTimeIsUniversal.Value.RawData.AsSpan());
    }

    private static RtcRegistryStateV1 ReadOnce()
    {
        if (!OperatingSystem.IsWindows())
            return Failure(ObservationAvailability.Unsupported, "RtcRegistryRequiresWindows");
        // KEY_QUERY_VALUE | KEY_WOW64_64KEY. Neither create nor set access is requested.
        var status = RegOpenKeyExW(unchecked((nint)(int)0x80000002), RtcRegistryStateV1.KeyPath,
            0, 0x0101, out var key);
        using (key)
        {
            if (status == 2)
                return new(Observations.Available(false),
                    Observations.Failure<RegistryValueV1>(ObservationAvailability.Absent, "RtcRegistryKeyAbsent"));
            if (status != 0) return Failure(ClassifyStatus(status), "RtcRegistryKeyQueryFailed");

            uint size = 0;
            status = RegQueryValueExW(key, RtcRegistryStateV1.ValueName, nint.Zero, out var type, null, ref size);
            if (status != 0) return ValueFailure(status);
            if (size > MaximumValueBytes)
                return new(Observations.Available(true), Observations.Failure<RegistryValueV1>(
                    ObservationAvailability.Unsupported, "RtcRegistryValueExceedsCaptureLimit"));
            // A non-null buffer distinguishes zero-byte values from a size-only query.
            var bytes = new byte[Math.Max(1, checked((int)size))];
            var capacity = size;
            status = RegQueryValueExW(key, RtcRegistryStateV1.ValueName, nint.Zero, out var capturedType, bytes, ref capacity);
            if (status != 0) return ValueFailure(status);
            if (type != capturedType || capacity > size)
                return new(Observations.Available(true), Observations.Failure<RegistryValueV1>(
                    ObservationAvailability.Ambiguous, "RtcRegistryValueChangedDuringRead"));
            return new(Observations.Available(true), Observations.Available(new RegistryValueV1(
                capturedType, bytes.AsSpan(0, checked((int)capacity)).ToArray().ToImmutableArray())));
        }
    }

    internal static ObservationAvailability ClassifyStatus(int status) => status switch
    {
        2 => ObservationAvailability.Absent,
        5 or 1300 or 1314 => ObservationAvailability.AccessDenied,
        1 or 50 => ObservationAvailability.Unsupported,
        234 or 1018 => ObservationAvailability.Ambiguous,
        _ => ObservationAvailability.Unavailable,
    };

    private static RtcRegistryStateV1 ValueFailure(int status) => new(Observations.Available(true),
        Observations.Failure<RegistryValueV1>(ClassifyStatus(status), status == 2
            ? "RtcRegistryValueAbsent" : "RtcRegistryValueQueryFailed"));

    private static RtcRegistryStateV1 Failure(ObservationAvailability availability, string reason) => new(
        Observations.Failure<bool>(availability, reason), Observations.Failure<RegistryValueV1>(availability, reason));

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyExW(nint key, string subKey, uint options, uint desiredAccess,
        out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueExW(SafeRegistryHandle key, string valueName, nint reserved,
        out uint type, [Out] byte[]? data, ref uint size);
}
