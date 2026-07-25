using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// All P/Invokes below target kernel32, a KnownDLL always resolved from System32.
// Pinning the search path to System32 defeats DLL-preloading (hijack) attacks.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Igloo.UsbWriter;

//   Native helpers

internal static partial class NativeMethods
{
    /// <summary>
    /// Opens a file or device (including raw physical drives such as
    /// <c>\\.\PHYSICALDRIVE1</c> and volume devices such as <c>\\.\C:</c>).
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileW(
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
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    /// <summary>Moves the file pointer of the specified file.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        nint lpNewFilePointer,   // may be null/Zero
        uint dwMoveMethod);      // 0 = FILE_BEGIN

    /// <summary>
    /// Reads data from a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        nint lpOverlapped);

    /// <summary>
    /// Writes data to a file using a synchronous (non-overlapped) handle.
    /// Pass <see cref="nint.Zero"/> for <c>lpOverlapped</c>.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        nint lpOverlapped);
}
