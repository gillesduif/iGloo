using System.ComponentModel;
using System.Diagnostics;

namespace Igloo.Migration;

/// <summary>One process of a browser, as far as ending it needs to know.</summary>
public sealed record BrowserProcess(bool HasWindow, Func<bool> End);

/// <summary>Finds which selected browsers still have a process, and ends one that has no window left.</summary>
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

    /// <summary>Ends the browsers that run without any window and returns their names; one with a window is left alone.</summary>
    public static IReadOnlyList<string> EndWindowless(IReadOnlyList<string> browsers) =>
        EndWindowless(browsers, ProcessesInThisSession);

    public static IReadOnlyList<string> EndWindowless(IReadOnlyList<string> browsers,
        Func<string, IReadOnlyList<BrowserProcess>> processesOf)
    {
        ArgumentNullException.ThrowIfNull(browsers);
        ArgumentNullException.ThrowIfNull(processesOf);
        var ended = new List<string>();
        foreach (var name in browsers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ProcessNames.TryGetValue(name, out var processNames)) continue;
            var processes = processNames.SelectMany(processesOf).ToArray();
            // A window may hold unsaved work; that browser is the user's to close.
            if (processes.Length == 0 || processes.Any(process => process.HasWindow)) continue;
            // Every process gets its turn even after one refuses.
            if (processes.Select(process => process.End()).ToArray().All(gone => gone))
                ended.Add(name);
        }
        return ended;
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

    // Another user's session is not this wizard's to touch.
    private static IReadOnlyList<BrowserProcess> ProcessesInThisSession(string processName)
    {
        using var self = Process.GetCurrentProcess();
        var found = new List<BrowserProcess>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != self.SessionId || process.HasExited) continue;
                    var id = process.Id;
                    found.Add(new(process.MainWindowHandle != IntPtr.Zero, () => End(id, processName)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Unreadable counts as a window: nothing is ended on a guess.
                    found.Add(new(true, () => false));
                }
            }
        }
        return found;
    }

    private static bool End(int id, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            // The browser's own process left and the id was handed to another program.
            if (process.HasExited || !string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                return true;
            process.Kill();
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No such process any more: it ended with its parent.
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
