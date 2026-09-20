using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace Igloo.Core.Abstractions;

public enum WindowsObservationStatus { Observed, Missing, Failed }

// Local provider evidence: retain native property values (including null) and errors.
// These are not Community projections, Fleet identity proofs, or network DTOs.
public sealed record WindowsObservation<T>(T? Value, Exception? Error = null) where T : class
{
    public WindowsObservationStatus Status => Error is not null ? WindowsObservationStatus.Failed :
        Value is null ? WindowsObservationStatus.Missing : WindowsObservationStatus.Observed;
    public T? ValueOrThrow()
    {
        if (Error is not null) ExceptionDispatchInfo.Capture(Error).Throw();
        return Value;
    }
}

public sealed record WindowsStorageRow(ImmutableDictionary<string, object?> Properties, string? ObjectPath = null)
{
    public object? this[string property] => Properties[property];
}

public sealed record WindowsStorageBatch(IReadOnlyList<WindowsStorageRow> Rows, Exception? Error = null)
{
    // Preserve partial enumeration followed by the original failure for legacy callers.
    public IEnumerable<WindowsStorageRow> RowsOrThrow()
    {
        foreach (var row in Rows) yield return row;
        if (Error is not null) ExceptionDispatchInfo.Capture(Error).Throw();
    }
}
