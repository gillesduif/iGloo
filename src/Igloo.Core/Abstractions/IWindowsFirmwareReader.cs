using System.Collections.Immutable;

namespace Igloo.Core.Abstractions;

// Native error is preserved; null data must not be interpreted as proof of absence.
public sealed record FirmwareVariableObservation(ImmutableArray<byte>? Data, int NativeError);

public interface IWindowsFirmwareReader
{
    FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096);
    FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096);
}
