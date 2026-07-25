using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Igloo.App.Converters;

public sealed class PartitionKindToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Fills = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows"] = Freeze("#FF2E86C4"),
        ["Data"] = Freeze("#FF49759C"),
        ["Efi"] = Freeze("#FF7A8894"),
        ["Msr"] = Freeze("#FF5A626B"),
        ["Recovery"] = Freeze("#FF6B7684"),
        ["Seed"] = Freeze("#FFC99A3F"),
        ["Linux"] = Freeze("#FF3F9E6E"),
        ["Unknown"] = Freeze("#FF4A5058"),
        ["Free"] = Freeze("#0AFFFFFF"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind && Fills.TryGetValue(kind, out var brush) ? brush : Fills["Unknown"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
