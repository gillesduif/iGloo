using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

/// <summary>Read-only composition over the existing canonical reader. Never enables privileges or writes firmware.</summary>
public sealed class WindowsFirmwareSnapshotCapture(IWindowsFirmwareReader reader)
{
    private readonly IWindowsFirmwareReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public FirmwareSnapshotV1 Capture(IEnumerable<ushort> requiredBootEntries)
    {
        ArgumentNullException.ThrowIfNull(requiredBootEntries);
        var required = requiredBootEntries.Distinct().Order().ToImmutableArray();
        var orderRaw = Read(() => _reader.ReadBootOrder(65536));
        var nextRaw = Read(() => _reader.ReadBootNext(65536));
        var order = EfiRecoveryParser.ParseBootOrder(orderRaw);
        var next = EfiRecoveryParser.ParseBootNext(nextRaw);
        var observed = new SortedSet<ushort>(required);
        if (order.Availability == ObservationAvailability.Available) observed.UnionWith(order.Value);
        if (next.Availability == ObservationAvailability.Available) observed.Add(next.Value);
        var entries = observed.Select(index => EfiRecoveryParser.ParseBootEntry(index,
            Read(() => _reader.ReadBootEntry(index, 65536)))).ToImmutableArray();
        return new(1, orderRaw, order, nextRaw, next, required, entries);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification =
        "Read-only provider boundary preserves managed observation failure; it never fabricates absence or native error codes.")]
    private static FirmwareVariableV1 Read(Func<FirmwareVariableObservation> read)
    {
        try { return EfiRecoveryParser.Observe(read()); }
        catch (Exception error)
        {
            int? nativeError = error is Win32Exception native ? native.NativeErrorCode : null;
            var availability = nativeError switch
            {
                5 or 1300 or 1314 => ObservationAvailability.AccessDenied,
                1 or 50 => ObservationAvailability.Unsupported,
                _ => ObservationErrors.Classify(error),
            };
            return new(Observations.Failure<ImmutableArray<byte>>(availability, "FirmwareReaderException"), nativeError,
                Observations.Failure<uint>(ObservationAvailability.Unsupported, "PropertyNotExposed"));
        }
    }
}
