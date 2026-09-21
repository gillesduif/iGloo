using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class BcdRecoveryReaderTests
{
    [Fact]
    public void MissingNativeValueCannotBecomeAnObservedZeroOrFalse()
    {
        Assert.Throws<FormatException>(() => WindowsBcdReader.RequireValue(null));
        Assert.Equal(0UL, WindowsBcdReader.RequireValue(0UL));
        Assert.Equal(false, WindowsBcdReader.RequireValue(false));
        Assert.Equal("", WindowsBcdReader.RequireValue(""));
    }
}
