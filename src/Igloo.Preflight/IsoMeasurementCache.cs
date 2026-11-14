namespace Igloo.Preflight;

// Sizes read from a mounted installer image, kept for the wizard's repeated space checks.
internal sealed class IsoMeasurementCache
{
    internal sealed record Key(string IsoPath, long Length, DateTime LastWriteUtc, string? IsoSha256, string Inputs);

    internal readonly record struct Sizes(long KernelBytes, long InitrdBytes, long ExtraBytes);

    private readonly object _gate = new();
    private (Key Key, Sizes Sizes)? _entry;

    public (Sizes Sizes, bool Reused) GetOrMeasure(Key key, Func<Sizes> measure)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(measure);
        lock (_gate)
        {
            if (_entry is { } entry && entry.Key == key)
                return (entry.Sizes, true);
        }
        var sizes = measure();
        lock (_gate)
            _entry = (key, sizes);
        return (sizes, false);
    }
}
