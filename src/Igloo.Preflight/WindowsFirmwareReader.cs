using System.Globalization;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed class WindowsFirmwareReader : IWindowsFirmwareReader
{
    internal static WindowsFirmwareReader Shared { get; } = new();
    private const string GlobalGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    // Privilege enablement remains at the existing callers' boundaries, including write flows.
    public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => ReadVariable("BootOrder", bufferBytes);
    public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096) =>
        ReadVariable("Boot" + index.ToString("X4", CultureInfo.InvariantCulture), bufferBytes);

    internal static FirmwareVariableObservation ReadVariable(string name, int bufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        var buffer = new byte[bufferBytes];
        var read = FirmwareNative.GetFirmwareEnvironmentVariableW(name, GlobalGuid, buffer, (uint)buffer.Length);
        var error = Marshal.GetLastWin32Error();
        return read == 0 ? new(null, error) : new(ImmutableArray.Create(buffer, 0, (int)read), 0);
    }
}
