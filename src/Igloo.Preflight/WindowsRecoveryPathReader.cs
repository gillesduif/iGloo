using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Microsoft.Win32.SafeHandles;

namespace Igloo.Preflight;

// Narrow native path correlation over canonical volume GUIDs. No mount assignment or writable handles.
public static class WindowsRecoveryPathReader
{
    public static Observation<Guid> ReadWindowsVolume()
    {
        if (!OperatingSystem.IsWindows()) return Observations.Failure<Guid>(ObservationAvailability.Unsupported, "WindowsRequired");
        var path = new char[1024];
        if (!GetVolumePathNameW(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path, 1024)) return Failed<Guid>("WindowsVolumePath");
        var volume = new char[1024];
        if (!GetVolumeNameForVolumeMountPointW(BufferString(path), volume, 1024)) return Failed<Guid>("WindowsVolumeGuid");
        return WindowsVolumeIdentity.TryParse(BufferString(volume), out var id) ? Observations.Available(id) :
            Observations.Failure<Guid>(ObservationAvailability.Ambiguous, "InvalidWindowsVolumeGuid");
    }

    public static Observation<CanonicalFileIdentityV1> ReadBootManagerFile(Guid volumeId)
    {
        if (volumeId == Guid.Empty) return Observations.Failure<CanonicalFileIdentityV1>(ObservationAvailability.Unavailable, "VolumeRequired");
        const string relative = @"\EFI\Microsoft\Boot\bootmgfw.efi";
        var prefix = @"\\?\Volume{" + volumeId.ToString("D") + "}";
        try
        {
            using var stream = new FileStream(prefix + relative, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resolved = new char[32768];
            var count = GetFinalPathNameByHandleW(stream.SafeFileHandle, resolved, (uint)resolved.Length, 1);
            if (count == 0) return Failed<CanonicalFileIdentityV1>("BootManagerHandlePath");
            if (count >= resolved.Length || !string.Equals(new string(resolved, 0, (int)count), prefix + relative, StringComparison.OrdinalIgnoreCase))
                return Observations.Failure<CanonicalFileIdentityV1>(ObservationAvailability.Ambiguous, "BootManagerPathMismatch");
            var length = stream.Length;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return stream.Length == length ? Observations.Available(new CanonicalFileIdentityV1(volumeId, relative, length, hash)) :
                Observations.Failure<CanonicalFileIdentityV1>(ObservationAvailability.Ambiguous, "BootManagerChangedDuringRead");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return Observations.Failure<CanonicalFileIdentityV1>(error is FileNotFoundException or DirectoryNotFoundException ? ObservationAvailability.Absent :
            ObservationErrors.Classify(error), "BootManagerFileUnavailable"); }
    }

    private static Observation<T> Failed<T>(string code)
    {
        var error = Marshal.GetLastWin32Error();
        return Observations.Failure<T>(error is 5 or 1300 or 1314 ? ObservationAvailability.AccessDenied :
            error is 1 or 50 ? ObservationAvailability.Unsupported : ObservationAvailability.Unavailable, code);
    }

    private static string BufferString(char[] buffer) => Array.IndexOf(buffer, '\0') is var end && end >= 0 ? new string(buffer, 0, end) : "";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}
