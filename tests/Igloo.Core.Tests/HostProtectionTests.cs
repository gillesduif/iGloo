using Igloo.Core.Safety;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class HostProtectionTests
{
    [Theory]
    [InlineData(null, HostProtectionState.NotProtected)]
    [InlineData(0, HostProtectionState.NotProtected)]
    [InlineData(1, HostProtectionState.Protected)]
    [InlineData(7, HostProtectionState.Protected)]
    [InlineData(0L, HostProtectionState.NotProtected)]
    [InlineData(1L, HostProtectionState.Protected)]
    [InlineData("1", HostProtectionState.Unreadable)]
    [InlineData("", HostProtectionState.Unreadable)]
    public void Marker_value_is_classified_without_guessing(object? value, HostProtectionState expected) =>
        Assert.Equal(expected, HostProtection.Classify(value));

    [Fact]
    public void Binary_marker_data_is_unreadable() =>
        Assert.Equal(HostProtectionState.Unreadable, HostProtection.Classify(new byte[] { 1 }));

    [Theory]
    [InlineData(HostProtectionState.Protected, "marked as a protected host")]
    [InlineData(HostProtectionState.Unreadable, "could not be read")]
    public void An_active_or_unreadable_marker_refuses_the_write_and_names_it(HostProtectionState state, string expected)
    {
        using var scope = HostProtection.UseForCurrentScope(state);

        var refused = Assert.Throws<HostProtectedException>(() => HostProtection.RequireWritable("partition deletion"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Not started: partition deletion.", refused.Message, StringComparison.Ordinal);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\iGloo, ProtectedHost", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_marker_the_write_may_start_and_nothing_is_shown()
    {
        using var scope = HostProtection.UseForCurrentScope(HostProtectionState.NotProtected);

        HostProtection.RequireWritable("partition deletion");

        Assert.Null(HostProtection.Describe(HostProtection.ReadState()));
    }

    [Fact]
    public void A_scoped_state_ends_with_its_scope()
    {
        var before = HostProtection.ReadState();
        using (HostProtection.UseForCurrentScope(HostProtectionState.Protected))
            Assert.Equal(HostProtectionState.Protected, HostProtection.ReadState());

        Assert.Equal(before, HostProtection.ReadState());
    }

    [Fact]
    public void Reading_the_machine_marker_never_throws() =>
        Assert.True(Enum.IsDefined(HostProtection.ReadMachineMarker()));
}
