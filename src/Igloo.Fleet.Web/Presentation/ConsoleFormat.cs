using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Igloo.Fleet.Web.Presentation;

/// <summary>
/// Identifiers, times and sizes as the console shows them. Times are absolute UTC in ISO order so
/// that an audit question has one answer regardless of where the console or the operator sits.
/// </summary>
internal static class ConsoleFormat
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    public static string ShortId(Guid id) => id.ToString("D")[..8];

    public static string UtcDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string UtcDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Relative(DateTimeOffset value, DateTimeOffset now, IStringLocalizer<SharedStrings> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var delta = now - value;
        var past = delta >= TimeSpan.Zero;
        var span = past ? delta : -delta;
        if (span < TimeSpan.FromMinutes(1))
        {
            return strings["JustNow"];
        }

        if (span < TimeSpan.FromHours(1))
        {
            return strings[past ? "MinutesAgo" : "InMinutes", (int)span.TotalMinutes];
        }

        if (span < TimeSpan.FromDays(1))
        {
            return strings[past ? "HoursAgo" : "InHours", (int)span.TotalHours];
        }

        var days = (int)span.TotalDays;
        return days == 1
            ? strings[past ? "DayAgo" : "InDay"]
            : strings[past ? "DaysAgo" : "InDays", days];
    }

    public static string Gibibytes(long bytes, IStringLocalizer<SharedStrings> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var value = bytes / (double)Gibibyte;
        var text = Math.Abs(value - Math.Round(value)) < 0.05
            ? Math.Round(value).ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("N1", CultureInfo.CurrentCulture);
        return strings["Gibibytes", text];
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
