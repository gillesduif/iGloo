using System.Globalization;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed class WindowsFirmwareReader : IWindowsFirmwareReader
{
    internal static WindowsFirmwareReader Shared { get; } = new();
    private const string GlobalGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    private readonly Func<int> _enableReadPrivilege;
    private readonly Func<string, int, FirmwareVariableObservation> _read;
    public WindowsFirmwareReader() : this(FirmwareNative.EnableReadPrivilege, ReadNativeVariable) { }
    internal WindowsFirmwareReader(Func<int> enableReadPrivilege, Func<string, int, FirmwareVariableObservation> read)
    { _enableReadPrivilege = enableReadPrivilege; _read = read; }

    public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => ReadWithPrivilege("BootOrder", bufferBytes);
    public FirmwareVariableObservation ReadBootNext(int bufferBytes = 4096) => ReadWithPrivilege("BootNext", bufferBytes);
    public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096) =>
        ReadWithPrivilege("Boot" + index.ToString("X4", CultureInfo.InvariantCulture), bufferBytes);

    internal static FirmwareVariableObservation ReadVariable(string name, int bufferBytes)
        => Shared.ReadWithPrivilege(name, bufferBytes);

    private static FirmwareVariableObservation ReadNativeVariable(string name, int bufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        var buffer = new byte[bufferBytes];
        var read = FirmwareNative.GetFirmwareEnvironmentVariableExW(name, GlobalGuid, buffer, (uint)buffer.Length, out var attributes);
        var error = Marshal.GetLastWin32Error();
        return ProjectNativeRead(buffer, read, error, attributes);
    }

    private FirmwareVariableObservation ReadWithPrivilege(string name, int bufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        var error = _enableReadPrivilege();
        if (error == 0) return _read(name, bufferBytes);
        // A privilege failure is never evidence that a firmware variable is absent.
        var state = error switch
        {
            5 or 1300 or 1314 => ObservationAvailability.AccessDenied,
            1 or 50 => ObservationAvailability.Unsupported,
            _ => ObservationAvailability.Unavailable,
        };
        return new(null, error)
        {
            FailureAvailability = state,
            VariableAttributes = Observations.Failure<uint>(state, "FirmwareReadPrivilegeUnavailable"),
        };
    }

    internal static FirmwareVariableObservation ProjectNativeRead(byte[] buffer, uint read, int error, uint attributes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (read > buffer.Length) throw new InvalidOperationException("Firmware returned a length beyond the read buffer.");
        var result = read == 0 ? new FirmwareVariableObservation(null, error) : new(ImmutableArray.Create(buffer, 0, (int)read), 0);
        return result with { VariableAttributes = read == 0
            ? Observations.Failure<uint>(result.Availability, "FirmwareAttributesReadFailed")
            : Observations.Available(attributes) };
    }
}
