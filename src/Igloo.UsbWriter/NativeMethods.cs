using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Igloo.UsbWriter;

// ── Native helpers ────────────────────────────────────────────────────────────

internal static partial class NativeMethods
{
    /// <summary>
    /// Opens a file or device (including raw physical drives such as
    /// <c>\\.\PHYSICALDRIVE1</c> and volume devices such as <c>\\.\C:</c>).
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Sends a control code directly to a device driver.
    /// Used here to issue volume FSCTL codes
    /// (<c>FSCTL_LOCK_VOLUME</c>, <c>FSCTL_DISMOUNT_VOLUME</c>),
    /// <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>, and
    /// <c>IOCTL_DISK_UPDATE_PROPERTIES</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    /// <summary>Moves the file pointer of the specified file.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        nint lpNewFilePointer,   // may be null/Zero
        uint dwMoveMethod);      // 0 = FILE_BEGIN

    /// <summary>
    /// Reads data from a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        nint lpOverlapped);

    /// <summary>
    /// Writes data to a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        nint lpOverlapped);
}
