using System.Globalization;
using System.Windows.Data;

namespace Igloo.App.Converters;

/// <summary>
/// MultiBinding converter: [part, whole, availableWidth] → pixel width, so partition
/// segments can be laid out proportionally to their byte size. Values may arrive as
/// any numeric type (long byte counts, int GiB, double widths).
/// </summary>
public sealed class ProportionalWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [var partRaw, var wholeRaw, var widthRaw]) return 0d;

        var part  = ToDouble(partRaw);
        var whole = ToDouble(wholeRaw);
        var width = ToDouble(widthRaw);

        if (whole <= 0 || width <= 0 || part <= 0) return 0d;
        return Math.Max(0, Math.Min(part / whole, 1.0) * width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        long l   => l,
        int i    => i,
        float f  => f,
        _        => 0d,
    };
}
