using FluentAssertions;
using Xunit;

namespace Igloo.Community.App.Tests;

public class AutoPlayGateTests
{
    private const uint Question = 0xC123;

    [Fact]
    public void The_question_is_declined_and_counted_only_inside_a_scope()
    {
        var gate = new AutoPlayGate();

        gate.Declines(unchecked((int)Question), Question).Should().BeFalse();

        using (gate.Begin())
        {
            gate.Declines(unchecked((int)Question), Question).Should().BeTrue();
            gate.Declines(unchecked((int)Question), Question).Should().BeTrue();
        }

        gate.Declines(unchecked((int)Question), Question).Should().BeFalse();
        gate.Declined.Should().Be(2);
    }

    [Fact]
    public void Another_message_is_left_to_the_window()
    {
        var gate = new AutoPlayGate();
        using var scope = gate.Begin();

        gate.Declines(0x0024, Question).Should().BeFalse();
        gate.Declined.Should().Be(0);
    }

    [Fact]
    public void An_unregistered_question_declines_nothing()
    {
        var gate = new AutoPlayGate();
        using var scope = gate.Begin();

        gate.Declines(0, 0).Should().BeFalse();
    }

    [Fact]
    public void A_scope_closed_twice_does_not_close_another_one()
    {
        var gate = new AutoPlayGate();
        var first = gate.Begin();
        using var second = gate.Begin();

        first.Dispose();
        first.Dispose();

        gate.Declines(unchecked((int)Question), Question).Should().BeTrue();
    }
}
