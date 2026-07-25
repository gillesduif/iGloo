using System.Globalization;
using FluentAssertions;
using Igloo.App.Converters;
using Xunit;

namespace Igloo.App.Tests;


public class BytesToSizeConverterTests
{
    private static string Convert(object value)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return (string)new BytesToSizeConverter()
                .Convert(value, typeof(string), null!, CultureInfo.InvariantCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2.0 TB")]
    [InlineData(512L * 1024 * 1024 * 1024, "512.0 GB")]
    [InlineData(128L * 1024 * 1024, "128 MB")]
    [InlineData(999L, "999 B")]
    [InlineData(0L, "0 B")]
    public void Formats_by_binary_magnitude(long bytes, string expected)
    {
        Convert(bytes).Should().Be(expected);
    }

    [Fact]
    public void Non_long_input_yields_an_empty_string()
    {
        Convert("not a number").Should().BeEmpty();
        Convert(123).Should().BeEmpty("int is not long; WPF bindings must pass long");
    }
}
