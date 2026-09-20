using System.Diagnostics;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed class WindowsBcdReader : IWindowsBcdReader
{
    public BcdListingObservation ReadFirmware()
    {
        using var process = Process.Start(new ProcessStartInfo(FindNativeExecutable("bcdedit.exe"), "/enum firmware")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return new(null, null, null);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return new(stdout, stderr, process.ExitCode);
    }

    internal static string FindNativeExecutable(string exeName)
    {
        var sysnative = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", exeName);
        return File.Exists(sysnative) ? sysnative : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
    }
}
