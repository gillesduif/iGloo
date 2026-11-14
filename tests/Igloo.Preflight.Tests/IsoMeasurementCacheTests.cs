using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class IsoMeasurementCacheTests
{
    private static readonly DateTime Written = new(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly IsoMeasurementCache.Sizes Measured = new(10, 20, 30);

    private static IsoMeasurementCache.Key Key(long length = 4096, string? sha = "AB", string inputs = "casper/vmlinuz") =>
        new(@"C:\iso\installer.iso", length, Written, sha, inputs);

    [Fact]
    public void The_same_image_is_measured_once()
    {
        var cache = new IsoMeasurementCache();
        var measurements = 0;

        var first = cache.GetOrMeasure(Key(), () => { measurements++; return Measured; });
        var second = cache.GetOrMeasure(Key(), () => { measurements++; return Measured; });

        Assert.Equal((Measured, false), first);
        Assert.Equal((Measured, true), second);
        Assert.Equal(1, measurements);
    }

    [Fact]
    public void A_changed_image_or_recipe_is_measured_again()
    {
        var changes = new[]
        {
            Key(length: 8192), Key(sha: "CD"), Key(inputs: "live/vmlinuz"),
            Key() with { LastWriteUtc = Written.AddSeconds(1) }, Key() with { IsoPath = @"C:\iso\other.iso" },
        };
        foreach (var changed in changes)
        {
            var cache = new IsoMeasurementCache();
            cache.GetOrMeasure(Key(), () => Measured);

            var (_, reused) = cache.GetOrMeasure(changed, () => Measured);

            Assert.False(reused);
        }
    }

    [Fact]
    public void A_failed_measurement_is_not_kept()
    {
        var cache = new IsoMeasurementCache();
        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrMeasure(Key(), () => throw new InvalidOperationException("the image did not mount")));

        var (sizes, reused) = cache.GetOrMeasure(Key(), () => Measured);

        Assert.Equal(Measured, sizes);
        Assert.False(reused);
    }
}
