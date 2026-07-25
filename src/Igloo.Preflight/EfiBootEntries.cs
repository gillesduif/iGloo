using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

// All P/Invokes in this assembly target kernel32/advapi32 (KnownDLLs resolved from
// System32). Pinning the search path defeats DLL-preloading (hijack) attacks.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Igloo.Preflight;

/// <summary>
/// Minimal read/delete access to UEFI <c>Boot####</c> NVRAM variables, shared by
/// the preflight checker (detect Linux boot entries) and the Linux removal
/// service (delete them). Locale-independent by design — parsing
/// <c>bcdedit /enum firmware</c> output breaks on non-English Windows.
///
/// All calls require <c>SeSystemEnvironmentPrivilege</c> (elevated process);
/// every method is best-effort and returns empty/false rather than throwing.
/// </summary>
internal static partial class EfiBootEntries
{
    private const string EfiGlobGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    internal readonly record struct BootEntry(ushort Index, string Description);

    //   Classification                            ─

    private static readonly string[] LinuxMarkers =
    [
        "ubuntu", "fedora", "debian", "mint", "suse", "manjaro", "grub", "arch",
        "pop!_os", "pop_os", "zorin", "elementary", "neon", "kylin", "deepin",
        "uos", "euler", "systemd-boot", "linux", "gentoo", "slackware",
        "endeavour", "nobara", "garuda", "cachy",
    ];

    /// <summary>True when the entry's description names a known Linux boot loader.</summary>
    internal static bool IsLinuxDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;
        if (description.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("igloo", StringComparison.OrdinalIgnoreCase))
            return false;
        return LinuxMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for iGloo's own one-shot installer entries.</summary>
    internal static bool IsIglooDescription(string description)
        => description.Contains("igloo", StringComparison.OrdinalIgnoreCase);

    //   Enumeration                              

    /// <summary>
    /// Returns every resolvable boot entry: the BootOrder list plus a sweep of
    /// the conventional 0x0000–0x00FF range (entries some firmwares keep outside
    /// BootOrder). Empty on any failure (no privilege, legacy BIOS, …).
    /// </summary>
    internal static IReadOnlyList<BootEntry> Enumerate(ILogger logger)
    {
        var entries = new List<BootEntry>();
        try
        {
            EnablePrivilege(logger);

            var indices = new SortedSet<ushort>(ReadBootOrder());
            for (ushort i = 0; i < 0x0100; i++)
                indices.Add(i);

            foreach (var index in indices)
            {
                var raw = ReadVariable($"Boot{index:X4}");
                if (raw is null)
                    continue;
                var description = ParseDescription(raw);
                if (description.Length > 0)
                    entries.Add(new BootEntry(index, description));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "UEFI boot-entry enumeration failed (non-fatal)");
        }
        return entries;
    }

    //   Deletion                               ─

    /// <summary>Deletes one Boot#### variable and drops it from BootOrder.</summary>
    internal static bool Delete(ushort index, ILogger logger)
    {
        try
        {
            EnablePrivilege(logger);

            if (!SetFirmwareEnvironmentVariableW($"Boot{index:X4}", EfiGlobGuid, null, 0))
            {
                logger.LogWarning("Delete of Boot{Index:X4} failed: Win32 error {Err}",
                    index, Marshal.GetLastWin32Error());
                return false;
            }

            var order = ReadBootOrder();
            if (order.Contains(index))
            {
                var remaining = order.Where(i => i != index).ToArray();
                var bytes = new byte[remaining.Length * 2];
                Buffer.BlockCopy(remaining, 0, bytes, 0, bytes.Length);
                if (!SetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, bytes, (uint)bytes.Length))
                    logger.LogWarning("BootOrder rewrite failed: Win32 error {Err} (non-fatal)",
                        Marshal.GetLastWin32Error());
            }

            logger.LogInformation("Deleted UEFI boot entry Boot{Index:X4}", index);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "Delete of Boot{Index:X4} failed (non-fatal)", index);
            return false;
        }
    }

    //   NVRAM plumbing                            ─

    private static byte[]? ReadVariable(string name)
    {
        var buf = new byte[4096];
        var read = GetFirmwareEnvironmentVariableW(name, EfiGlobGuid, buf, (uint)buf.Length);
        if (read == 0)
            return null;
        var result = new byte[read];
        Buffer.BlockCopy(buf, 0, result, 0, (int)read);
        return result;
    }

    private static ushort[] ReadBootOrder()
    {
        var raw = ReadVariable("BootOrder");
        if (raw is null || raw.Length < 2)
            return [];
        var order = new ushort[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, order, 0, order.Length * 2);
        return order;
    }

    /// <summary>
    /// EFI_LOAD_OPTION: UINT32 Attributes · UINT16 FilePathListLength ·
    /// null-terminated CHAR16 Description · device path. We only need the description.
    /// </summary>
    internal static string ParseDescription(byte[] loadOption)
    {
        const int descStart = 6;
        if (loadOption.Length <= descStart)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = descStart; i + 1 < loadOption.Length; i += 2)
        {
            var ch = (char)(loadOption[i] | (loadOption[i + 1] << 8));
            if (ch == '\0')
                break;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static void EnablePrivilege(ILogger logger)
    {
        // TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, out var token))
        {
            logger.LogWarning("OpenProcessToken failed: {Err}", Marshal.GetLastWin32Error());
            return;
        }
        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out var luid))
                return;
            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 2 };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint PrivilegeCount; public Luid Luid; public uint Attributes; }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[]? pValue, uint nSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
