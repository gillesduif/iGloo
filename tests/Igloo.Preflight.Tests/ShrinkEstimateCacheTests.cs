using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ShrinkEstimateCacheTests
{
    [Fact]
    public async Task A_young_answer_is_reused_per_disk_and_an_old_one_is_asked_again()
    {
        var now = TimeSpan.Zero;
        var asked = new List<int>();
        var cache = new ShrinkEstimateCache(TimeSpan.FromMinutes(15), () => now);
        Task<(long Bytes, bool Reused)> Get(int disk, long answer, bool fresh = false) =>
            cache.GetAsync(disk, fresh, () => { asked.Add(disk); return Task.FromResult(answer); });

        Assert.Equal((100L, false), await Get(0, 100));
        now = TimeSpan.FromMinutes(14);
        Assert.Equal((100L, true), await Get(0, 200));
        Assert.Equal((300L, false), await Get(1, 300));
        now = TimeSpan.FromMinutes(15);
        Assert.Equal((200L, false), await Get(0, 200));

        Assert.Equal([0, 1, 0], asked);
    }

    [Fact]
    public async Task The_read_ahead_always_asks_and_a_preparation_forgets_everything()
    {
        var asked = 0;
        var cache = new ShrinkEstimateCache(TimeSpan.FromMinutes(15), () => TimeSpan.Zero);
        Task<(long Bytes, bool Reused)> Get(long answer, bool fresh) =>
            cache.GetAsync(0, fresh, () => { asked++; return Task.FromResult(answer); });

        await Get(100, fresh: false);
        Assert.Equal((200L, false), await Get(200, fresh: true));
        Assert.Equal((200L, true), await Get(300, fresh: false));

        cache.Clear();
        Assert.Equal((400L, false), await Get(400, fresh: false));
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task A_failed_query_keeps_nothing()
    {
        var cache = new ShrinkEstimateCache(TimeSpan.FromMinutes(15), () => TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetAsync(0, fresh: false, () => Task.FromException<long>(new InvalidOperationException("no answer"))));

        Assert.Equal((5L, false), await cache.GetAsync(0, fresh: false, () => Task.FromResult(5L)));
    }
}
