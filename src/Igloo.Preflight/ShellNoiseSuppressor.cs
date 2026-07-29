using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

/// <summary>
/// Keeps Windows' shell quiet while Igloo creates and populates its staging partitions.
/// </summary>
/// <remarks>
/// Creating a lettered volume makes Windows announce it: AutoPlay fires and, depending
/// on the user's settings, an Explorer window opens on the new drive. During a direct
/// install that means File Explorer windows popping up over the wizard mid-operation
/// and an OEMDRV window left sitting open afterwards - which reads as the installer
/// having done something wrong, on precisely the screen where the user is most anxious.
///
/// The partitions genuinely need drive letters (diskpart assigns them, and the staging
/// code addresses files through them), so the fix is to silence the announcement rather
/// than avoid the letter.
///
/// Scope is deliberately small: only the current user's AutoRun policy, only for the
/// duration of the install, and always restored in <see cref="Dispose"/> - including
/// when the value was previously unset, in which case the value is removed again rather
/// than left behind as a zero.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ShellNoiseSuppressor : IDisposable
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string ValueName = "NoDriveTypeAutoRun";

    // 0xFF = every drive type. AutoRun is suppressed for all of them while this object
    // lives; anything narrower would still let removable/fixed media through, which is
    // exactly the class our staging partitions fall into.
    private const int SuppressAllDriveTypes = 0xFF;

    private readonly ILogger _logger;
    private readonly bool _applied;
    private readonly object? _previousValue;

    public ShellNoiseSuppressor(ILogger logger)
    {
        _logger = logger;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
            if (key is null)
                return;

            _previousValue = key.GetValue(ValueName);
            key.SetValue(ValueName, SuppressAllDriveTypes, RegistryValueKind.DWord);
            _applied = true;
            _logger.LogInformation("AutoPlay suppressed for this user while the installer partitions are created");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Cosmetic only - an Explorer window popping up is not a reason to fail an
            // install, so this never throws.
            _logger.LogWarning(ex, "Could not suppress AutoPlay (non-fatal) - Explorer may open on the new volumes");
        }
    }

    /// <summary>
    /// Closes any Explorer window showing <paramref name="driveLetter"/>.
    /// </summary>
    /// <remarks>
    /// Belt to the AutoPlay braces: a window opened before suppression took effect, or
    /// by something other than AutoRun, is still sitting there when the install
    /// finishes. Driven through the shell's own window collection via late binding, so
    /// the project needs no COM interop reference for a purely cosmetic tidy-up.
    /// </remarks>
    public void CloseExplorerWindowsFor(char driveLetter)
    {
        var root = $"{char.ToUpperInvariant(driveLetter)}:\\";
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return;

            foreach (dynamic window in shell.Windows())
            {
                string? path = null;
                try
                {
                    // LocationURL is empty for Internet Explorer-style windows; only
                    // file:// locations are folder views we might want to close.
                    string url = window.LocationURL;
                    if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                        path = uri.LocalPath;
                }
                catch (Exception ex) when (ex is RuntimeBinderException or COMException)
                {
                    continue;   // not a folder window
                }

                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        window.Quit();
                        _logger.LogInformation("Closed the Explorer window left open on {Root}", root);
                    }
                    catch (COMException)
                    {
                        // The user may have closed it first; nothing to do.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException
                                      or NotSupportedException or RuntimeBinderException)
        {
            _logger.LogWarning(ex, "Could not close Explorer windows for {Root} (non-fatal)", root);
        }
    }

    public void Dispose()
    {
        if (!_applied)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyKey, writable: true);
            if (key is null)
                return;

            if (_previousValue is null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, _previousValue);

            _logger.LogInformation("AutoPlay setting restored");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not restore the AutoPlay setting - it stays suppressed for this user");
        }
    }
}
