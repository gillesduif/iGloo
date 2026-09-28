using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Igloo.Distro.Debian.Deployment;

// Linux statx has a fixed-width, documented ABI. Verify trust BEFORE invoking either
// the Python interpreter or the privileged gate, not only inside the invoked helper.
internal static partial class DebianSessionToolProtection
{
    internal static void Verify(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Qualified amd64 Linux session required.");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            throw new InvalidDataException("Noncanonical runtime path.");
        Check(path, false);
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent)) Check(parent, true);
    }

    private static unsafe void Check(string path, bool directory)
    {
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        fixed (byte* data = buffer)
        {
            // AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_MODE | STATX_UID.
            if (Statx(-100, path, 0x100, 0x0b, data) != 0) throw new IOException("Runtime ownership observation unavailable.");
        }
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]);
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]);
        if ((mask & 0x0b) != 0x0b || uid != 0 || (mode & 0xf000) != (directory ? 0x4000 : 0x8000) ||
            (directory ? (mode & 0x12) != 0 && (mode & 0x200) == 0 : (mode & 0xc12) != 0))
            throw new InvalidDataException("Runtime tool or parent is not protected.");
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);
}
