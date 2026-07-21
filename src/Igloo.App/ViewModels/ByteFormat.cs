namespace Igloo.App.ViewModels;

/// <summary>
/// Compact size formatting for view-model display strings (GB/MB/KB, binary units).
/// Not the same scale as <c>BytesToSizeConverter</c>, which additionally handles TB
/// and raw bytes for XAML bindings.
/// </summary>
internal static class ByteFormat
{
    internal static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
        _ => $"{bytes / 1024.0:N0} KB",
    };
}
