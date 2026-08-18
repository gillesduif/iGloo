using System.Security.Cryptography;
using CryptSharp;

namespace Igloo.Core.Services;

/// <summary>Produces the crypt(3) hashes the installers accept in place of a plain-text password.</summary>
/// <remarks>
/// The preseed, kickstart and autoinstall files land on a FAT32 seed partition that has no
/// access control, so a plain-text password there is readable by anyone holding the medium.
/// Debian's installation guide states the same and calls a hashed password secure; every
/// installer iGloo targets accepts one (passwd/user-password-crypted, user --iscrypted,
/// identity.password), so the plain text never has to travel.
///
/// SHA-512-crypt rather than yescrypt: the algorithm has been frozen since 2008 and ships
/// seven published test vectors, which LinuxPasswordHasherTests checks one by one. Fedora
/// guarantees the format keeps verifying after its switch to yescrypt, and Debian recommends
/// SHA-512 outright. A wrong hash locks the user out of the machine they just migrated to,
/// so verifiability outweighs modernity here.
/// </remarks>
public static class LinuxPasswordHasher
{
    // glibc caps the salt at 16 characters and ignores anything beyond it.
    private const int SaltChars = 16;

    // The crypt(3) base64 alphabet, in its own peculiar order.
    private const string SaltAlphabet =
        "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Hashes <paramref name="password"/> as SHA-512-crypt with a fresh random salt.</summary>
    /// <returns>A <c>$6$</c> modular crypt string, or null when there is no password to hash.</returns>
    public static string? Sha512Crypt(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return null;

        return Crypter.Sha512.Crypt(password, "$6$" + GenerateSalt());
    }

    private static string GenerateSalt()
    {
        var salt = new char[SaltChars];
        for (int i = 0; i < salt.Length; i++)
            salt[i] = SaltAlphabet[RandomNumberGenerator.GetInt32(SaltAlphabet.Length)];
        return new string(salt);
    }
}
