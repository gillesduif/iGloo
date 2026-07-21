namespace Igloo.Core.Services;

/// <summary>
/// Rate-limits an <see cref="IProgress{T}"/> chain.
///
/// Byte-level operations (ISO download, hashing, staging copies) report per
/// buffer — hundreds of reports per second for minutes. Each report posted to
/// the UI thread triggers text re-layout, which is wasted work at best and, in
/// the worst case, hammers WPF's glyph pipeline exactly when it is fragile
/// (Windows updates restarting the FontCache service mid-session have crashed
/// the app from <c>TextBlock.Measure</c>). Forwarding at ~10 Hz is
/// indistinguishable to a human and removes ~99% of that pressure.
///
/// Reports matching <paramref name="forceWhen"/> (phase transitions, completion)
/// always pass through, so the final "100%" is never lost to the throttle.
/// <see cref="Report"/> is safe to call from any thread.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly Func<T, T?, bool> _forceWhen;
    private readonly long _intervalTicks;
    private long _lastForwardedTicks = long.MinValue;
    private T? _lastForwarded;
    private readonly object _gate = new();

    /// <param name="inner">Destination (typically a UI-marshalling <see cref="Progress{T}"/>).</param>
    /// <param name="interval">Minimum time between forwarded reports (default 100 ms).</param>
    /// <param name="forceWhen">
    /// Given (current, lastForwarded — null before the first forward), returns true to
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
            long now = DateTime.UtcNow.Ticks;
            if (!_forceWhen(value, _lastForwarded) && now - _lastForwardedTicks < _intervalTicks)
                return;
            _lastForwardedTicks = now;
            _lastForwarded = value;
        }
        _inner.Report(value);
    }
}
