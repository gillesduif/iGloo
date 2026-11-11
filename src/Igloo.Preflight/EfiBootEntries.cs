using System.ComponentModel;
using Igloo.Core.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

// All P/Invokes in this assembly target kernel32/advapi32 (KnownDLLs resolved from
// System32). Pinning the search path defeats DLL-preloading (hijack) attacks.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Igloo.Preflight;

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

    
    internal static bool IsLinuxDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;
        if (description.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("igloo", StringComparison.OrdinalIgnoreCase))
            return false;
        return LinuxMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    
    internal static bool IsIglooDescription(string description)
        => description.Contains("igloo", StringComparison.OrdinalIgnoreCase);

    //   Enumeration                              

    internal static IReadOnlyList<BootEntry> Enumerate(ILogger logger)
    {
        var entries = new List<BootEntry>();
        try
        {
            EnablePrivilege(logger);

            entries.AddRange(EnumerateObserved(WindowsFirmwareReader.Shared));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "UEFI boot-entry enumeration failed (non-fatal)");
        }
        return entries;
    }

    /// <param name="PartitionGuid">The GPT partition the entry's device path names; null when it names none.</param>
    internal readonly record struct BootEntryTarget(ushort Index, string Description, Guid? PartitionGuid);

    internal static IReadOnlyList<BootEntryTarget> EnumerateTargets(ILogger logger)
    {
        try
        {
            EnablePrivilege(logger);
            return EnumerateTargetsObserved(WindowsFirmwareReader.Shared).ToArray();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "UEFI boot-entry enumeration failed (non-fatal)");
            return [];
        }
    }

    internal static IEnumerable<BootEntryTarget> EnumerateTargetsObserved(IWindowsFirmwareReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var indices = new SortedSet<ushort>(DecodeBootOrder(reader.ReadBootOrder().Data?.ToArray()));
        for (ushort i = 0; i < 0x0100; i++)
            indices.Add(i);

        foreach (var index in indices)
        {
            var raw = reader.ReadBootEntry(index).Data?.ToArray();
            if (raw is null)
                continue;
            var description = ParseDescription(raw);
            if (description.Length > 0)
                yield return new BootEntryTarget(index, description, ParsePartitionGuid(raw));
        }
    }

    // The GPT partition in the first hard-drive node of a load option's device path.
    internal static Guid? ParsePartitionGuid(byte[] loadOption)
    {
        ArgumentNullException.ThrowIfNull(loadOption);
        if (loadOption.Length < 8)
            return null;
        var pathLength = loadOption[4] | (loadOption[5] << 8);
        var position = 6;
        while (position + 1 < loadOption.Length && (loadOption[position] != 0 || loadOption[position + 1] != 0))
            position += 2;
        position += 2;
        var end = Math.Min(loadOption.Length, position + pathLength);
        while (position + 4 <= end)
        {
            var length = loadOption[position + 2] | (loadOption[position + 3] << 8);
            if (length < 4 || position + length > end || loadOption[position] == 0x7F)
                return null;
            if (loadOption[position] == 4 && loadOption[position + 1] == 1 && length >= 42 && loadOption[position + 41] == 2)
                return new Guid(loadOption.AsSpan(position + 24, 16));
            position += length;
        }
        return null;
    }

    internal static IEnumerable<BootEntry> EnumerateObserved(IWindowsFirmwareReader reader)
    {
        var indices = new SortedSet<ushort>(DecodeBootOrder(reader.ReadBootOrder().Data?.ToArray()));
        for (ushort i = 0; i < 0x0100; i++)
            indices.Add(i);

        foreach (var index in indices)
        {
            var raw = reader.ReadBootEntry(index).Data?.ToArray();
            if (raw is null)
                continue;
            var description = ParseDescription(raw);
            if (description.Length > 0)
                yield return new BootEntry(index, description);
        }
    }

    /// <summary>
    /// Every Boot#### index whose load option targets the partition with
    /// <paramref name="partitionGuid"/>, regardless of how the entry is described.
    /// </summary>
    /// <remarks>
    /// Needed because some firmware (observed on Gigabyte/AMI) DELETES a custom
    /// Boot#### at POST and silently substitutes its own auto-generated entry -
    /// typically described "UEFI OS" - pointing at the very same
    /// \EFI\BOOT\BOOTX64.EFI target. The staged installer is therefore still
    /// bootable, but under an index we never wrote and did not know to prioritise.
    /// Matching on the partition rather than the description finds that substitute,
    /// so the handoff can survive our own entry being culled.
    ///
    /// The match looks for the partition's signature GUID inside the load option:
    /// an EFI device path stores it verbatim in its HARDDRIVE node, so its presence
    /// identifies the target without needing a full device-path parser.
    /// </remarks>
    internal static IReadOnlyList<ushort> FindEntriesTargetingPartition(Guid partitionGuid, ILogger logger)
    {
        var matches = new List<ushort>();
        if (partitionGuid == Guid.Empty)
            return matches;

        var needle = partitionGuid.ToByteArray();
        try
        {
            EnablePrivilege(logger);
            for (ushort index = 0; index < 0x0100; index++)
            {
                var raw = ReadVariable($"Boot{index:X4}");
                if (raw is not null && ContainsSequence(raw, needle))
                    matches.Add(index);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "Could not scan UEFI entries for the installer partition (non-fatal)");
        }
        return matches;
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            var hit = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    hit = false;
                    break;
                }
            }
            if (hit)
                return true;
        }
        return false;
    }

    //   Deletion                               ─

    
    internal static bool Delete(ushort index, ILogger logger)
    {
        try
        {
            EnablePrivilege(logger);

            if (!FirmwareNative.SetFirmwareEnvironmentVariableW($"Boot{index:X4}", EfiGlobGuid, null, 0))
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
                if (!FirmwareNative.SetFirmwareEnvironmentVariableW("BootOrder", EfiGlobGuid, bytes, (uint)bytes.Length))
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

    private static byte[]? ReadVariable(string name) => WindowsFirmwareReader.ReadVariable(name, 4096).Data?.ToArray();

    private static ushort[] ReadBootOrder() => DecodeBootOrder(ReadVariable("BootOrder"));

    internal static ushort[] DecodeBootOrder(byte[]? raw)
    {
        if (raw is null || raw.Length < 2)
            return [];
        var order = new ushort[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, order, 0, order.Length * 2);
        return order;
    }

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

    private static void EnablePrivilege(ILogger logger) => FirmwareNative.EnablePrivilege(logger, reportAssignmentFailures: false);
}
