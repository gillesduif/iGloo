using System.Buffers.Binary;
using System.Text;
using Igloo.Core.Abstractions;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    // An installer kernel that follows the distribution is taken only with an image whose own kernel is of the same release.
    internal static void RequireImageOfInstallerKernel(string isoRoot, InstallerBootSpec bootSpec,
        PinnedInstallerFile installerKernel)
    {
        if (bootSpec.MediaIndex is not { } index)
            return;
        var installer = installerKernel.ReadKernelRelease();
        string? image = null;
        var imageKernel = InstallerMediaIndex.IsPlainPath(index.ImageKernelPath)
            ? Path.Join(isoRoot, index.ImageKernelPath.Replace('/', Path.DirectorySeparatorChar))
            : null;
        if (File.Exists(imageKernel))
        {
            using var source = new FileStream(imageKernel, FileMode.Open, FileAccess.Read, FileShare.Read);
            image = LinuxKernelImage.ReadRelease(source);
        }
        // Two unreadable releases are not two equal ones.
        if (installer is null || image is null)
            throw InstallationRefusedException.InstallerKernelUnreadable(installer, image);
        if (!string.Equals(installer, image, StringComparison.Ordinal))
            throw InstallationRefusedException.InstallerAndImageDiffer(installer, image);
    }
}

internal static class LinuxKernelImage
{
    private const int SetupHeader = 0x200;

    // The release a bzImage names in its setup header, such as "6.12.117+deb13-amd64"; null when the file is not one.
    internal static string? ReadRelease(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Span<byte> header = stackalloc byte[SetupHeader + 0x10];
        image.Position = 0;
        if (image.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length ||
            !header.Slice(SetupHeader + 2, 4).SequenceEqual("HdrS"u8))
            return null;
        var pointer = BinaryPrimitives.ReadUInt16LittleEndian(header[(SetupHeader + 0xE)..]);
        if (pointer == 0)
            return null;
        Span<byte> text = stackalloc byte[128];
        image.Position = SetupHeader + pointer;
        var read = image.ReadAtLeast(text, text.Length, throwOnEndOfStream: false);
        // The release is the first word of the version string.
        var end = text[..read].IndexOfAny((byte)0, (byte)' ');
        if (end <= 0)
            return null;
        var release = Encoding.ASCII.GetString(text[..end]);
        return release.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or '_' or '~') ? release : null;
    }
}
