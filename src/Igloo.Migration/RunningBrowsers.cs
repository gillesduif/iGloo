using System.ComponentModel;
using System.Diagnostics;

namespace Igloo.Migration;

/// <summary>Finds which selected browsers still have a process. Reads the process list only; iGloo never closes a browser.</summary>
public static class RunningBrowsers
{
    // Windows process names, keyed by the browser name the wizard lists.
    private static readonly Dictionary<string, string[]> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Edge"] = ["msedge"],
        ["Google Chrome"] = ["chrome"],
        ["Mozilla Firefox"] = ["firefox"],
        ["Brave"] = ["brave"],
        ["Vivaldi"] = ["vivaldi"],
        ["Opera"] = ["opera"],
        ["Zen Browser"] = ["zen"],
        ["Waterfox"] = ["waterfox"],
    };

    public static IReadOnlyList<string> Find(IReadOnlyList<string> selectedBrowsers) => Find(selectedBrowsers, IsRunning);

    public static IReadOnlyList<string> Find(IReadOnlyList<string> selectedBrowsers, Func<string, bool> isRunning)
    {
        ArgumentNullException.ThrowIfNull(selectedBrowsers);
        ArgumentNullException.ThrowIfNull(isRunning);
        return [.. selectedBrowsers.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => ProcessNames.TryGetValue(name, out var processes) && processes.Any(isRunning))];
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
                process.Dispose();
            return processes.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // An unreadable process list does not block staging; the copy keeps its own lock handling.
            return false;
        }
    }
}
