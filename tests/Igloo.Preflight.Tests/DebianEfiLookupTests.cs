using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DebianEfiLookupTests
{
    [Fact]
    public void Debian_live_iso_fallback_boot_file_is_accepted_as_shim()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-efi-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Debian Live 13.7 stores these with lowercase `boot` and
            // `bootx64.efi`; Windows mounted ISO paths are case-insensitive.
            var efi = OperatingSystem.IsWindows()
                ? Path.Join(root, "EFI", "boot")
                : Path.Join(root, "EFI", "BOOT");
            Directory.CreateDirectory(efi);
            File.WriteAllBytes(Path.Join(efi, OperatingSystem.IsWindows() ? "bootx64.efi" : "BOOTX64.EFI"), [1]);
            File.WriteAllBytes(Path.Join(efi, "grubx64.efi"), [2]);

            var (shim, grub) = DirectInstallService.FindEfiFiles(root);

            File.ReadAllBytes(shim).Should().Equal([1]);
            File.ReadAllBytes(grub).Should().Equal([2]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fedora_vendor_shim_retains_priority_over_fallback()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-efi-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vendor = Path.Join(root, "EFI", "fedora");
            var fallback = Path.Join(root, "EFI", "BOOT");
            Directory.CreateDirectory(vendor);
            Directory.CreateDirectory(fallback);
            File.WriteAllBytes(Path.Join(vendor, "shimx64.efi"), [1]);
            File.WriteAllBytes(Path.Join(vendor, "grubx64.efi"), [2]);
            File.WriteAllBytes(Path.Join(fallback, "BOOTX64.EFI"), [3]);
            File.WriteAllBytes(Path.Join(fallback, "grubx64.efi"), [4]);

            var (shim, grub) = DirectInstallService.FindEfiFiles(root);

            File.ReadAllBytes(shim).Should().Equal([1]);
            File.ReadAllBytes(grub).Should().Equal([2]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
