namespace Igloo.App.ViewModels;

internal static class ByteFormat
{
    internal static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
        _ => $"{bytes / 1024.0:N0} KB",
    };
}
