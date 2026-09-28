using System.Collections.Immutable;

namespace Igloo.Core.Abstractions;

// Native error is preserved; null data must not be interpreted as proof of absence.
public sealed record FirmwareVariableObservation(ImmutableArray<byte>? Data, int NativeError)
{
    // Legacy/fake providers that only expose bytes must not acquire assumed attributes.
    public Observation<uint> VariableAttributes { get; init; } =
        Observations.Failure<uint>(ObservationAvailability.Unsupported, "PropertyNotExposed");

    // Pre-acquisition failure (for example token access) is not variable absence, even if
    // that different native API reports error 203. Only the getter may establish absence.
    public ObservationAvailability? FailureAvailability { get; init; }

    public ObservationAvailability Availability => FailureAvailability ?? (Data.HasValue && !Data.Value.IsDefault ? ObservationAvailability.Available : NativeError switch
    {
        203 => ObservationAvailability.Absent,
        5 or 1314 or 1300 => ObservationAvailability.AccessDenied,
        1 or 50 => ObservationAvailability.Unsupported,
        _ => ObservationAvailability.Unavailable,
    });

    public Observation<ushort> DecodeBootNext()
    {
        if (Availability != ObservationAvailability.Available) return Observations.Failure<ushort>(Availability, "FirmwareRead");
        var bytes = Data!.Value;
        return bytes.Length == 2 ? Observations.Available<ushort>((ushort)(bytes[0] | bytes[1] << 8)) :
            Observations.Failure<ushort>(ObservationAvailability.Ambiguous, "MalformedBootNext");
    }
}

public interface IWindowsFirmwareReader
{
    FirmwareVariableObservation ReadBootNext(int bufferBytes = 4096) => new(null, 50);
    FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096);
    FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096);
}
