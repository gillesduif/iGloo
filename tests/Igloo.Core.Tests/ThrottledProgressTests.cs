using FluentAssertions;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

/// <summary>
/// Tests for <see cref="ThrottledProgress{T}"/>. Contract: the first report is
/// always forwarded (regardless of any force predicate), reports inside the
/// interval are suppressed, and reports matching the force predicate always pass
/// through. The predicate is evaluated on every report.
/// </summary>
public class ThrottledProgressTests
{
    /// <summary>Synchronous recorder; <see cref="Progress{T}"/> would marshal asynchronously.</summary>
    private sealed class Recorder : IProgress<string>
    {
        public List<string> Reports { get; } = [];
        public void Report(string value) => Reports.Add(value);
    }

    [Fact]
    public void First_report_is_always_forwarded_even_without_a_force_predicate()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1));

        throttled.Report("a");

        recorder.Reports.Should().Equal("a");
    }

    [Fact]
    public void Reports_inside_the_interval_are_suppressed_after_the_first()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1));

        throttled.Report("a");
        throttled.Report("b");
        throttled.Report("c");

        recorder.Reports.Should().Equal("a");
    }

    [Fact]
    public void Reports_flow_again_once_the_interval_has_elapsed()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.Zero);

        throttled.Report("a");
        throttled.Report("b");

        recorder.Reports.Should().Equal("a", "b");
    }

    [Fact]
    public void Force_predicate_bypasses_the_throttle()
    {
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1),
            forceWhen: (current, _) => current == "final");

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

    [Fact]
    public void Production_convention_prev_is_null_forcing_still_behaves()
    {
        // Both real call sites pass forceWhen: (cur, prev) => prev is null || ...;
        // the fix must not change what they observe.
        var recorder = new Recorder();
        var throttled = new ThrottledProgress<string>(recorder, TimeSpan.FromHours(1),
            forceWhen: (_, prev) => prev is null);

        throttled.Report("a");
        throttled.Report("b");

        recorder.Reports.Should().Equal("a");
    }
}
