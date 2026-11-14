using System.Runtime.Versioning;
using System.Security;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

/// <summary>Windows Fast Startup, which iGloo turns off for a dual boot and puts back when Linux is removed.</summary>
[SupportedOSPlatform("windows")]
internal static class FastStartup
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string PowerValue = "HiberbootEnabled";
    private const string StateKey = @"SOFTWARE\iGloo";
    private const string StateValue = "FastStartupBeforeDualBoot";

    // Remembered when Windows had no value of its own.
    internal const int NotSet = -1;

    /// <summary>Turns it off and remembers the earlier value once; false when it was already off.</summary>
    internal static bool TurnOff(int? current, int? remembered, Action<int> remember, Action<int> write)
    {
        ArgumentNullException.ThrowIfNull(remember);
        ArgumentNullException.ThrowIfNull(write);
        if (current == 0)
            return false;
        // What an earlier installation remembered is the value from before iGloo; it stays.
        if (remembered is null)
            remember(current ?? NotSet);
        write(0);
        return true;
    }

    /// <summary>Puts back what iGloo changed; false when iGloo changed nothing.</summary>
    internal static bool Restore(int? remembered, Action<int> write, Action delete, Action forget)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(forget);
        if (remembered is null)
            return false;
        if (remembered == NotSet)
            delete();
        else
            write(remembered.Value);
        forget();
        return true;
    }

    internal static void TurnOffForDualBoot(ILogger logger)
    {
        HostProtection.RequireWritable("HiberbootEnabled registry value");
        try
        {
            using var power = Registry.LocalMachine.CreateSubKey(PowerKey);
            using var state = Registry.LocalMachine.CreateSubKey(StateKey);
            if (TurnOff(power.GetValue(PowerValue) as int?, state.GetValue(StateValue) as int?,
                    earlier => state.SetValue(StateValue, earlier, RegistryValueKind.DWord),
                    value => power.SetValue(PowerValue, value, RegistryValueKind.DWord)))
                logger.LogInformation(
                    "Fast Startup turned off (HiberbootEnabled=0) - Windows now shuts down fully, " +
                    "so Linux does not open a hibernated Windows volume");
            else
                logger.LogInformation("Fast Startup was already off");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex,
                "Could not turn Fast Startup off (non-fatal) - after a normal Windows shutdown " +
                "the Windows volume must not be opened from Linux");
        }
    }

    internal static void RestoreAfterLinux(ILogger logger)
    {
        HostProtection.RequireWritable("HiberbootEnabled registry value");
        try
        {
            using var state = Registry.LocalMachine.OpenSubKey(StateKey, writable: true);
            if (state is null)
                return;
            using var power = Registry.LocalMachine.CreateSubKey(PowerKey);
            if (Restore(state.GetValue(StateValue) as int?,
                    value => power.SetValue(PowerValue, value, RegistryValueKind.DWord),
                    () => power.DeleteValue(PowerValue, throwOnMissingValue: false),
                    () => state.DeleteValue(StateValue, throwOnMissingValue: false)))
                logger.LogInformation("Fast Startup put back as it was before the installation");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not put Fast Startup back (non-fatal)");
        }
    }
}
