using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
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
    /// finishes. Driven through the shell's own window collection, using the hand-written
    /// declarations in ShellComInterop.cs rather than a generated interop assembly.
    /// </remarks>
    public void CloseExplorerWindowsFor(char driveLetter)
    {
        var root = $"{char.ToUpperInvariant(driveLetter)}:\\";
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                _logger.LogDebug("Shell.Application is not registered - nothing to close");
                return;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is not IShellDispatch dispatch)
            {
                _logger.LogError("Shell.Application does not expose IShellDispatch - "
                                 + "check the IID in ShellComInterop.cs");
                return;
            }

            object? windowsObj = null;
            try
            {
                windowsObj = dispatch.Windows();
                if (windowsObj is not IShellWindows windows)
                {
                    _logger.LogError("Windows() did not return IShellWindows - check the "
                                     + "IID in ShellComInterop.cs");
                    return;
                }

                _logger.LogDebug("Shell reports {Count} open window(s)", windows.Count);

                // Count/Item, not foreach: enumerating needs IEnumVARIANT marshalled by
                // hand, which costs more than it saves for a handful of windows.
                for (int i = 0; i < windows.Count; i++)
                    CloseIfUnder(windows, i, root);
            }
            finally
            {
                if (windowsObj is not null)
                    Marshal.ReleaseComObject(windowsObj);
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException
                                      or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not close Explorer windows for {Root} (non-fatal)", root);
        }
    }

    private void CloseIfUnder(IShellWindows windows, int index, string root)
    {
        object? item = null;
        try
        {
            item = windows.Item(index);
            // Item() hands back IDispatch; Internet Explorer windows are in the same
            // collection and do not implement IWebBrowser2's folder behaviour.
            if (item is not IWebBrowser2 window)
                return;

            var url = window.LocationURL;
            if (string.IsNullOrEmpty(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !uri.IsFile
                || !uri.LocalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return;

            window.Quit();
            _logger.LogInformation("Closed the Explorer window left open on {Root}", root);
        }
        catch (COMException ex)
        {
            LogComFailure(ex, index);
        }
        finally
        {
            if (item is not null)
                Marshal.ReleaseComObject(item);
        }
    }

    // DISP_E_UNKNOWNNAME / DISP_E_MEMBERNOTFOUND / E_NOINTERFACE mean the hand-written
    // declarations in ShellComInterop.cs are wrong - the same class of bug the old
    // dynamic code raised as RuntimeBinderException, and it must not pass quietly.
    private void LogComFailure(COMException ex, int index)
    {
        switch ((uint)ex.HResult)
        {
            case 0x80020006:  // DISP_E_UNKNOWNNAME
            case 0x80020003:  // DISP_E_MEMBERNOTFOUND
            case 0x80004002:  // E_NOINTERFACE
                _logger.LogError(ex,
                    "Shell interop declaration is wrong: window {Index} rejected the call "
                    + "with HRESULT 0x{HResult:X8}. Check the IIDs and member names in "
                    + "ShellComInterop.cs against HKCR\\Interface", index, ex.HResult);
                break;
            default:
                _logger.LogDebug(ex,
                    "Skipped window {Index}: HRESULT 0x{HResult:X8}", index, ex.HResult);
                break;
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
