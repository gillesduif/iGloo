using System.Collections.Concurrent;
using System.Diagnostics;

namespace Igloo.Preflight;

/// <summary>Keeps Windows' shrink limit per disk for a short time, for the estimate on Target Disk only.</summary>
internal sealed class ShrinkEstimateCache(TimeSpan lifetime, Func<TimeSpan>? clock = null)
{
    private readonly ConcurrentDictionary<int, (long Bytes, TimeSpan At)> _kept = new();
    private readonly Stopwatch _running = Stopwatch.StartNew();

    private TimeSpan Now => clock?.Invoke() ?? _running.Elapsed;

    /// <summary>The kept value when it is young enough and <paramref name="fresh"/> is false; otherwise a new query.</summary>
    public async Task<(long Bytes, bool Reused)> GetAsync(int diskNumber, bool fresh, Func<Task<long>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!fresh && _kept.TryGetValue(diskNumber, out var kept) && Now - kept.At < lifetime)
            return (kept.Bytes, true);
        var bytes = await query().ConfigureAwait(false);
        _kept[diskNumber] = (bytes, Now);
        return (bytes, false);
    }

    public void Clear() => _kept.Clear();
}
