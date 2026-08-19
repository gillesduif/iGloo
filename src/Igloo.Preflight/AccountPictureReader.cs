using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Igloo.Preflight;

/// <summary>
/// Locates the Windows account picture so it can become the Linux user's avatar.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="WallpaperReader"/>: seeing your own face on the login
/// screen of a fresh install reads as "my computer" rather than "a foreign system".
///
/// Windows keeps the picture as a set of pre-scaled JPEGs under
/// <c>C:\Users\Public\AccountPictures\{SID}\</c>, and records the paths per size in
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{SID}</c>
/// as <c>Image32</c> … <c>Image1080</c>. The registry is the primary source because it
/// names the file the shell is actually using; the directory listing is the fallback
/// for accounts where the key is missing. The largest size wins - Linux greeters scale
/// down, and a 448px avatar upscaled from 96px looks exactly as bad as it sounds.
///
/// Users who never set a picture have no key and no directory, and the migration simply
/// skips it.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AccountPictureReader
{
    private const string RegistryRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users";

    private const string PublicPictures = @"C:\Users\Public\AccountPictures";

    // A 32px JPEG is roughly 1.5 KB; anything under 512 bytes is a stub. The upper
    // bound keeps a pathological file off the FAT32 seed partition.
    private const long MinBytes = 512;
    private const long MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Returns the path of the largest usable account picture, or null when the user
    /// has none. Never throws - a missing avatar must not break staging.
    /// </summary>
    public static string? TryFindAccountPicture(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var sid = CurrentUserSid(logger);
            if (sid is null)
                return null;

            var fromRegistry = LargestFromRegistry(sid, logger);
            if (fromRegistry is not null)
                return fromRegistry;

            var fromDisk = LargestOnDisk(sid);
            if (fromDisk is not null)
            {
                logger.LogInformation(
                    "No AccountPicture registry entry for this SID; using the largest file on disk");
                return fromDisk;
            }

            logger.LogInformation("No account picture found - the distro default avatar will be kept");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Account picture detection failed - skipping (non-fatal)");
            return null;
        }
    }

    private static string? CurrentUserSid(ILogger logger)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid))
                logger.LogWarning("Could not determine the current user's SID");
            return sid;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Could not determine the current user's SID");
            return null;
        }
    }

    /// <summary>Picks the highest ImageNNN value whose file is present and readable.</summary>
    private static string? LargestFromRegistry(string sid, ILogger logger)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{RegistryRoot}\{sid}");
            if (key is null)
                return null;

            return key.GetValueNames()
                .Select(name => (Name: name, Size: ParseImageSize(name)))
                .Where(v => v.Size > 0)
                .OrderByDescending(v => v.Size)
                .Select(v => key.GetValue(v.Name) as string)
                .FirstOrDefault(IsUsableImage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "Could not read the AccountPicture registry key");
            return null;
        }
    }

    /// <summary>"Image448" -> 448; anything else -> 0.</summary>
    private static int ParseImageSize(string valueName) =>
        valueName.StartsWith("Image", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(valueName.AsSpan(5), out var size)
            ? size
            : 0;

    private static string? LargestOnDisk(string sid)
    {
        var dir = Path.Join(PublicPictures, sid);
        if (!Directory.Exists(dir))
            return null;

        return Directory.EnumerateFiles(dir, "*.jpg")
            .Where(IsUsableImage)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static bool IsUsableImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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
