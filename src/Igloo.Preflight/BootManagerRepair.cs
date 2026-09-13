using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>
/// Points the firmware's Windows Boot Manager entry at the Linux bootloader.
/// </summary>
/// <remarks>
/// Choosing Windows from the GRUB menu leaves Windows first in the UEFI boot
/// order, so the next power-on runs bootmgfw.efi directly and GRUB never
/// appears - the machine looks like Linux was never installed. Re-ordering the
/// entries from Linux cannot fix that, because by then Linux is the thing that
/// no longer boots.
///
/// So instead of fighting for first place, the entry that keeps taking it is
/// made to launch shim. <c>bcdedit /set {bootmgr} path</c> rewrites the loader
/// path of the Windows Boot Manager firmware entry; whatever Windows does to the
/// boot order afterwards is then harmless. GRUB still chainloads bootmgfw.efi by
/// file path, so Windows itself keeps booting normally.
///
/// A Windows feature update can reset the BCD and revert this, at which point it
/// has to be applied again. See docs/reference/boot-menu.md.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class BootManagerRepair
{
    /// <summary>The stock value, and what <see cref="Revert"/> restores.</summary>
    public const string WindowsBootManagerPath = @"\EFI\Microsoft\Boot\bootmgfw.efi";

    /// <summary>
    /// Loader path for a distribution's shim on the EFI system partition.
    /// </summary>
    /// <remarks>
    /// Signed shim rather than grubx64.efi: on a Secure Boot machine only shim is
    /// trusted by the firmware, and it is shim that then verifies GRUB.
    /// </remarks>
    public static string ShimPathFor(string efiDirectoryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(efiDirectoryName);
        return $@"\EFI\{efiDirectoryName}\shimx64.efi";
    }

    /// <summary>
    /// Whether <paramref name="loaderPath"/> exists on the EFI system partition
    /// mounted at <paramref name="espRoot"/>.
    /// </summary>
    /// <remarks>
    /// Checked before anything is written, never assumed. Pointing the firmware at
    /// a file that is not there leaves a machine that boots neither system, which
    /// is a far worse outcome than the menu going missing.
    /// </remarks>
    public static bool LoaderExists(string espRoot, string loaderPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(espRoot);
        ArgumentException.ThrowIfNullOrEmpty(loaderPath);
        return File.Exists(Path.Join(espRoot, loaderPath.TrimStart('\\')));
    }

    /// <summary>
    /// The loader path the Windows Boot Manager entry currently points at, or null
    /// when the listing does not name one.
    /// </summary>
    internal static string? ParseCurrentPath(string? bcdeditFirmwareListing)
    {
        if (string.IsNullOrEmpty(bcdeditFirmwareListing))
            return null;

        // The {bootmgr} block, up to the blank line that ends it. bcdedit writes
        // CRLF, so the separator has to tolerate both - the same trap that once
        // made the stale-entry cleanup delete a single entry per run.
        foreach (var block in Regex.Split(bcdeditFirmwareListing, @"\r?\n[ \t]*\r?\n"))
        {
            if (!block.Contains("{bootmgr}", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = Regex.Match(block, @"^path\s+(\S+)", RegexOptions.Multiline);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }
}
