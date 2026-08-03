using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

/// <summary>
/// Locates the image behind the Windows desktop wallpaper so it can travel with the
/// migration and become the Linux desktop background.
/// </summary>
/// <remarks>
/// Why this exists: a fresh Linux install greeting the user with their OWN desktop
/// background is a small thing that reads as "my computer, just different" instead of
/// "a foreign system". It costs almost nothing to carry.
///
/// The primary source is <c>HKCU\Control Panel\Desktop\Wallpaper</c>. That value is
/// empty for solid-colour backgrounds and points at a transient cache for slideshows
/// and Windows Spotlight; in those cases we fall back to
/// <c>%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper</c>, the decoded copy of
/// whatever is currently on screen (JPEG content, extensionless name). If neither
/// yields a readable image, the migration simply skips the wallpaper - the distro
/// default is a perfectly fine outcome.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WallpaperReader
{
    // Wallpapers are photos; anything smaller is almost certainly a broken cache file,
    // anything larger has no business travelling to a FAT32 seed partition.
    private const long MinBytes = 10 * 1024;
    private const long MaxBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    /// <summary>
    /// Returns the path of a readable wallpaper image, or null when there is nothing
    /// sensible to migrate. Never throws - a missing wallpaper must not break staging.
    /// </summary>
    public static string? TryFindWallpaper(ILogger logger)
    {
        try
        {
            var fromRegistry = ReadRegistryWallpaper(logger);
            if (IsUsableImage(fromRegistry, requireExtension: true))
                return fromRegistry;

            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            // The transcoded copy has no extension but is JPEG/PNG content.
            if (IsUsableImage(transcoded, requireExtension: false))
            {
                logger.LogInformation(
                    "Wallpaper registry value unusable ({RegistryValue}); using the transcoded copy",
                    string.IsNullOrWhiteSpace(fromRegistry) ? "(empty)" : fromRegistry);
                return transcoded;
            }

            logger.LogInformation("No migratable wallpaper found - the distro default will be kept");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Wallpaper detection failed - skipping (non-fatal)");
            return null;
        }
    }

    private static string? ReadRegistryWallpaper(ILogger logger)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Could not read the wallpaper registry value");
            return null;
        }
    }

    private static bool IsUsableImage(string? path, bool requireExtension)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (requireExtension && !ImageExtensions.Contains(Path.GetExtension(path)))
            return false;
        try
        {
            var length = new FileInfo(path).Length;
            return length is >= MinBytes and <= MaxBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
