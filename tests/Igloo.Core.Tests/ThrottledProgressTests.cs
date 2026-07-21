using FluentAssertions;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

/// <summary>
/// Characterization tests for <see cref="ThrottledProgress{T}"/>. These pin CURRENT
/// behavior, including a known quirk: the initial <c>_lastForwardedTicks = long.MinValue</c>
/// makes <c>now - _lastForwardedTicks</c> overflow negative, so WITHOUT a force predicate
/// no report is ever forwarded. Both production call sites mask this by passing
/// <c>forceWhen: (cur, prev) =&gt; prev is null || …</c>, which forces the first report
/// through and initializes the timestamp. See REFACTOR_NOTES.md before "fixing" this.
/// The type parameter is a reference type here, as in production; for value types the
/// <c>prev is null</c> convention cannot apply.
/// </summary>
public class ThrottledProgressTests
{
    /// <summary>Synchronous recorder; <see cref="Progress{T}"/> would marshal asynchronously.</summary>
    private sealed class Recorder : IProgress<string>
    {
        public List<string> Reports { get; } = [];
        public void Report(string value) => Reports.Add(value);
    }

    /// <summary>The convention every production call site uses.</summary>
    private static ThrottledProgress<string> ProductionStyle(Recorder recorder, TimeSpan interval)
        => new(recorder, interval, forceWhen: (_, prev) => prev is null);

    [Fact]
    public void Known_quirk_without_force_predicate_nothing_is_forwarded()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromMilliseconds(1));

        throttled.Report("a");
        throttled.Report("b");

        recorder.Reports.Should().BeEmpty(
            "the long.MinValue seed overflows the interval arithmetic; " +
            "callers must force the first report through (all current ones do)");
    }

    [Fact]
    public void First_report_is_forwarded_under_the_production_convention()
    {
        var recorder = new Recorder();
        var throttled = ProductionStyle(recorder, TimeSpan.FromHours(1));

        throttled.Report("a");

        recorder.Reports.Should().Equal("a");
    }

    [Fact]
    public void Reports_inside_the_interval_are_suppressed_after_the_first()
    {
        var recorder = new Recorder();
        var throttled = ProductionStyle(recorder, TimeSpan.FromHours(1));

        throttled.Report("a");
        throttled.Report("b");
        throttled.Report("c");

        recorder.Reports.Should().Equal("a");
    }

    [Fact]
    public void Force_predicate_bypasses_the_throttle()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1),
            forceWhen: (current, prev) => prev is null || current == "final");

        throttled.Report("a");
        throttled.Report("b");
        throttled.Report("final");

        recorder.Reports.Should().Equal("a", "final");
    }

    [Fact]
    public void Force_predicate_receives_the_last_forwarded_value()
    {
        var recorder = new Recorder();
        var seen = new List<string?>();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1),
            forceWhen: (_, last) =>
            {
                seen.Add(last);
                return true;
            });

        throttled.Report("x");
        throttled.Report("y");

        seen.Should().Equal(null, "x");
        recorder.Reports.Should().Equal("x", "y");
    }
}
