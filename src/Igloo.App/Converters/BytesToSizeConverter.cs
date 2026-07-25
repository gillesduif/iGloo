using System.Globalization;
using System.Windows.Data;

namespace Igloo.App.Converters;


[ValueConversion(typeof(long), typeof(string))]
public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
            return string.Empty;

        return bytes switch
        {
            >= 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):N1} TB",
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
            _ => $"{bytes:N0} B",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
