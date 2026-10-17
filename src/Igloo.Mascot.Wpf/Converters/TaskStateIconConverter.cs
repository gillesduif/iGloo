using System.Globalization;
using System.Windows.Data;
using Igloo.Mascot.Core;

namespace Igloo.Mascot.Wpf.Converters;

[ValueConversion(typeof(TaskCardState), typeof(string))]
public sealed class TaskStateIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TaskCardState state ? state switch
        {
            TaskCardState.Busy => "↻",
            TaskCardState.Done => "✓",
            TaskCardState.Failed => "×",
            _ => string.Empty
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
