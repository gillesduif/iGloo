using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// All P/Invokes below target kernel32, a KnownDLL always resolved from System32.
// Pinning the search path to System32 defeats DLL-preloading (hijack) attacks.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Igloo.UsbWriter;

//   Native helpers

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

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

    
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        nint lpNewFilePointer,   // may be null/Zero
        uint dwMoveMethod);      // 0 = FILE_BEGIN

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        nint lpOverlapped);
}
