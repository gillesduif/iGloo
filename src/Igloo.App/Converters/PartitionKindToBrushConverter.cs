using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Igloo.App.Converters;

/// <summary>
/// Maps a partition-segment kind (see <c>PreflightViewModel.ClassifyPartition</c>)
/// to its fill brush in the disk bar and legend.
///
/// Charter note: this is the data-visualization exception to "semantic color
/// only" — a partition map needs categorical color to be readable at all.
/// The palette stays muted and dark-theme native; the only loud entries carry
/// meaning: accent blue = the Windows install, amber = iGloo's own seed
/// partitions, green = Linux.
/// </summary>
public sealed class PartitionKindToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Fills = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows"]  = Freeze("#FF2E86C4"),
        ["Data"]     = Freeze("#FF49759C"),
        ["Efi"]      = Freeze("#FF7A8894"),
        ["Msr"]      = Freeze("#FF5A626B"),
        ["Recovery"] = Freeze("#FF6B7684"),
        ["Seed"]     = Freeze("#FFC99A3F"),
        ["Linux"]    = Freeze("#FF3F9E6E"),
        ["Unknown"]  = Freeze("#FF4A5058"),
        ["Free"]     = Freeze("#0AFFFFFF"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string kind && Fills.TryGetValue(kind, out var brush) ? brush : Fills["Unknown"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
