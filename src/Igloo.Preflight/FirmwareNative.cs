using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

internal static partial class FirmwareNative
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[]? pValue, uint nSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(
        string? lpSystemName, string lpName, out long lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TokenPrivileges NewState, uint Length,
        IntPtr PreviousState, IntPtr ReturnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    // Pack=4 is critical: on x64 .NET, LayoutKind.Sequential without Pack inserts
    // 4 bytes of padding after uint PrivilegeCount to align the long Luid field to
    // 8 bytes, producing a 24-byte struct.  Win32 TOKEN_PRIVILEGES is 16 bytes with
    // no padding.  Without Pack=4 the LUID is at the wrong offset and
    // AdjustTokenPrivileges silently receives a garbage LUID, leaving
    // SeSystemEnvironmentPrivilege disabled → ERROR_PRIVILEGE_NOT_HELD (1314).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;            // LUID = LowPart (4 b) + HighPart (4 b) at offset 4
        public uint Attributes;      // SE_PRIVILEGE_ENABLED = 2, at offset 12
    }

    internal static void EnablePrivilege(ILogger logger, bool reportAssignmentFailures)
    {
        // TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, out var token))
        {
            logger.LogWarning("OpenProcessToken failed: {Err}", Marshal.GetLastWin32Error());
            return; // will fail at SetFirmwareEnvironmentVariable with a clear Win32 error
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out var luid))
            {
                if (reportAssignmentFailures) logger.LogWarning("LookupPrivilegeValue(SeSystemEnvironmentPrivilege) failed: {Err}",
                    Marshal.GetLastWin32Error());
                return;
            }

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = 2,  // SE_PRIVILEGE_ENABLED
            };

            // AdjustTokenPrivileges returns TRUE even when not all privileges were
            // assigned - check GetLastError for ERROR_NOT_ALL_ASSIGNED (1300).
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            var adjustErr = Marshal.GetLastWin32Error();
            if (reportAssignmentFailures && adjustErr != 0)
                logger.LogWarning(
                    "AdjustTokenPrivileges(SeSystemEnvironmentPrivilege) returned error {Err} - " +
                    "UEFI NVRAM write will likely fail. Is the process running as Administrator?",
                    adjustErr);
        }
        finally
        {
            CloseHandle(token);
        }
    }

}
