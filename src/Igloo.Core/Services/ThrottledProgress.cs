namespace Igloo.Core.Services;

public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly Func<T, T?, bool> _forceWhen;
    private readonly long _intervalTicks;
    private long _lastForwardedTicks;
    private bool _hasForwarded;
    private T? _lastForwarded;
    private readonly object _gate = new();

    /// <param name="inner">Destination (typically a UI-marshalling <see cref="Progress{T}"/>).</param>
    /// <param name="interval">Minimum time between forwarded reports (default 100 ms).</param>
    /// <param name="forceWhen">
    /// Given (current, lastForwarded  null before the first forward), returns true to
    /// bypass the throttle. Use for phase changes and completion reports.
    /// </param>
    public ThrottledProgress(IProgress<T> inner, TimeSpan? interval = null,
                             Func<T, T?, bool>? forceWhen = null)
    {
        _inner = inner;
        _intervalTicks = (interval ?? TimeSpan.FromMilliseconds(100)).Ticks;
        _forceWhen = forceWhen ?? ((_, _) => false);
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            // The predicate is evaluated on every report (callers may rely on
            // seeing each value); _hasForwarded guards the very first report,
            // which must never be throttled.
            long now = DateTime.UtcNow.Ticks;
            bool force = _forceWhen(value, _lastForwarded);
            if (!force && _hasForwarded && now - _lastForwardedTicks < _intervalTicks)
                return;
            _hasForwarded = true;
            _lastForwardedTicks = now;
            _lastForwarded = value;
        }
        _inner.Report(value);
    }
}
