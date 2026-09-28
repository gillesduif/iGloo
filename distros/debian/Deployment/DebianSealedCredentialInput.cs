using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Igloo.Distro.Debian.Deployment;

// The UI/runtime credential broker supplies an already sealed memfd, never a path or argv
// value. This class owns its duplicate, is single-use, and persists no secret evidence.
public sealed partial class DebianSealedCredentialInput : IDebianCredentialInput, IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _used;

    public DebianSealedCredentialInput(SafeFileHandle protectedDescriptor)
    {
        ArgumentNullException.ThrowIfNull(protectedDescriptor);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Sealed credential input requires Linux.");
        var retained = false;
        try
        {
            protectedDescriptor.DangerousAddRef(ref retained);
            var duplicate = Fcntl(protectedDescriptor.DangerousGetHandle().ToInt32(), 1030, 3); // F_DUPFD_CLOEXEC
            if (duplicate < 0) throw new IOException("Credential descriptor unavailable.");
            _handle = new SafeFileHandle(duplicate, true);
            // F_SEAL_SEAL | SHRINK | GROW | WRITE. Unsupported regular files fail here.
            if ((Fcntl(duplicate, 1034, 0) & 15) != 15 || Fcntl(duplicate, 1034, 0) < 0)
            {
                _handle.Dispose();
                throw new IOException("Credential descriptor must be sealed against modification.");
            }
        }
        finally { if (retained) protectedDescriptor.DangerousRelease(); }
    }

    public async Task SendEncryptedPasswordAsync(string expectedArtifactSha256, string username, Stream processInput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processInput);
        if (_used) throw new InvalidOperationException("Credential input cannot be retried.");
        _used = true;
        if (!DebianDeploymentPlanning.Hash(expectedArtifactSha256) || string.IsNullOrEmpty(username) || username.Length > 32 ||
            !char.IsAsciiLetterLower(username[0]) || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("Invalid credential binding.");
        byte[] secret = new byte[1025];
        byte[] input = new byte[1060];
        try
        {
            var length = RandomAccess.GetLength(_handle);
            if (length is < 20 or > 1024) throw new InvalidDataException("Invalid encrypted credential artifact.");
            var read = 0;
            while (read < length)
            {
                var count = await RandomAccess.ReadAsync(_handle, secret.AsMemory(read, (int)length - read), read, ct).ConfigureAwait(false);
                if (count == 0) throw new IOException("Credential input incomplete.");
                read += count;
            }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret.AsSpan(0, read)), Convert.FromHexString(expectedArtifactSha256)) ||
                secret[0] != '$' || secret[2] != '$' || secret[1] is not ((byte)'6' or (byte)'y') ||
                secret.Take(read).Any(c => !char.IsAsciiLetterOrDigit((char)c) && c is not ((byte)'$' or (byte)'.' or (byte)'/' or (byte)'=')))
                throw new InvalidDataException("Encrypted credential binding or format rejected.");
            var prefix = Encoding.ASCII.GetBytes(username + ":", input);
            secret.AsSpan(0, read).CopyTo(input.AsSpan(prefix));
            input[prefix + read] = (byte)'\n';
            await processInput.WriteAsync(input.AsMemory(0, prefix + read + 1), ct).ConfigureAwait(false);
            await processInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(input);
            _handle.Dispose();
        }
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fcntl(int descriptor, int command, int argument);
}
