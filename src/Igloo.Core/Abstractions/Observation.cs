namespace Igloo.Core.Abstractions;

public enum ObservationAvailability { Available, Unavailable, Unsupported, AccessDenied, Ambiguous, Absent }

// No failed fact exposes a default zero/false as an observed value.
public sealed record Observation<T>
{
    private readonly T? _value;
    internal Observation(ObservationAvailability availability, T? value, string? code)
    { Availability = availability; _value = value; Code = code; }
    public ObservationAvailability Availability { get; }
    public string? Code { get; }
    public T Value => Availability == ObservationAvailability.Available ? _value! :
        throw new InvalidOperationException("Observation is not available.");
}

public static class Observations
{
    public static Observation<T> Available<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ObservationAvailability.Available, value, null);
    }
    public static Observation<T> Failure<T>(ObservationAvailability availability, string code)
    {
        if (availability == ObservationAvailability.Available) throw new ArgumentOutOfRangeException(nameof(availability));
        return new(availability, default, code);
    }
}

public static class ObservationErrors
{
    public static ObservationAvailability Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            UnauthorizedAccessException or System.Security.SecurityException => ObservationAvailability.AccessDenied,
            NotSupportedException => ObservationAvailability.Unsupported,
            _ when error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80041003) => ObservationAvailability.AccessDenied,
            _ when error.HResult is unchecked((int)0x8004100C) or unchecked((int)0x80041010) => ObservationAvailability.Unsupported,
            _ => ObservationAvailability.Unavailable,
        };
    }

    public static ObservationAvailability SupportedSizeReturn(uint code) => code switch
    {
        0 => ObservationAvailability.Available,
        1 or 4097 or 42009 => ObservationAvailability.Unsupported,
        40001 => ObservationAvailability.AccessDenied,
        _ => ObservationAvailability.Unavailable,
    };
}
