using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class MountedIsoTests : IDisposable
{
    private const string Volume = @"\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\";
    private readonly string _directory = Path.Join(Path.GetTempPath(), "igloo-mounted-iso-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_image_is_attached_read_only_and_without_a_drive_letter()
    {
        var command = MountedIso.MountCommand("'C:\\Users\\O''Brien\\debian.iso'", driveLetter: false);

        Assert.Equal("Mount-DiskImage -ImagePath 'C:\\Users\\O''Brien\\debian.iso' -Access ReadOnly -NoDriveLetter", command);
        Assert.DoesNotContain("-NoDriveLetter", MountedIso.MountCommand("'x.iso'", driveLetter: true), StringComparison.Ordinal);
        Assert.Contains("-Access ReadOnly", MountedIso.MountCommand("'x.iso'", driveLetter: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_one_volume_GUID_root_counts_as_the_mounted_image()
    {
        Assert.Equal(Volume, MountedIso.VolumeRoot(Volume + "\r\n"));
        Assert.Equal(Volume, MountedIso.VolumeRoot("  " + Volume));

        Assert.Null(MountedIso.VolumeRoot(""));
        Assert.Null(MountedIso.VolumeRoot("E:\\"));
        Assert.Null(MountedIso.VolumeRoot("E"));
        Assert.Null(MountedIso.VolumeRoot(Volume + "\r\n" + Volume));
        Assert.Null(MountedIso.VolumeRoot(Volume.TrimEnd('\\')));
        Assert.Null(MountedIso.VolumeRoot(Volume + "live"));
        Assert.Null(MountedIso.VolumeRoot(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\"));
        Assert.Null(MountedIso.VolumeRoot(@"\\?\Volume{not-a-volume-identifier-of-36-chars}\"));
    }

    [WindowsVolumeFact]
    public void The_image_readers_find_and_measure_files_through_a_volume_GUID_root()
    {
        var root = VolumeGuidPath(_directory) + "\\";
        Write(Path.Join("live", "vmlinuz"), 11);
        Write(Path.Join("live", "initrd.img"), 65);
        Write(Path.Join("EFI", "boot", "bootx64.efi"), 3);
        Write(Path.Join("EFI", "boot", "grubx64.efi"), 4);
        Write(Path.Join("images", "install.img"), 870);
        var spec = Spec() with { ExtraIsoFiles = [new IsoFileStage("images/install.img", "images/install.img", true)] };

        var (kernel, initrd) = DirectInstallService.FindKernelFiles(root, spec, NullLogger.Instance);
        var (shim, grub) = DirectInstallService.FindEfiFiles(root);

        Assert.StartsWith(@"\\?\Volume{", root, StringComparison.Ordinal);
        Assert.Equal(11, new FileInfo(kernel).Length);
        Assert.Equal(65, new FileInfo(initrd).Length);
        Assert.Equal(3, new FileInfo(shim).Length);
        Assert.Equal(4, new FileInfo(grub).Length);
        Assert.Equal(870, DirectInstallService.MeasureRequiredIsoContent(root, spec));
    }

    [WindowsVolumeFact]
    public void A_kernel_outside_the_declared_places_is_found_by_the_scan_through_a_volume_GUID_root()
    {
        var root = VolumeGuidPath(_directory) + "\\";
        Write(Path.Join("casper", "hwe", "vmlinuz"), 7);
        Write(Path.Join("casper", "hwe", "initrd"), 9);
        var (kernel, initrd) = DirectInstallService.FindKernelFiles(root, Spec(), NullLogger.Instance);

        Assert.Equal(root + @"casper\hwe\vmlinuz", kernel);
        Assert.Equal(root + @"casper\hwe\initrd", initrd);
    }

    private static InstallerBootSpec Spec() => new()
    {
        MenuTitle = "Install",
        KernelCmdline = "quiet",
        KernelIsoPaths = ["live/vmlinuz"],
        InitrdIsoPaths = ["live/initrd.img"],
    };

    private void Write(string relative, int bytes)
    {
        var path = Path.Join(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // The same directory, addressed through the GUID of the Windows volume instead of its drive letter.
    private static string VolumeGuidPath(string directory)
    {
        var volume = WindowsRecoveryPathReader.ReadWindowsVolume();
        Assert.Equal(ObservationAvailability.Available, volume.Availability);
        return @"\\?\Volume{" + volume.Value.ToString("D") + "}" + directory[2..];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class WindowsVolumeFactAttribute : FactAttribute
    {
        public WindowsVolumeFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Volume GUID paths exist on Windows only.";
            else if (!string.Equals(Path.GetPathRoot(Path.GetTempPath()),
                         Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                         StringComparison.OrdinalIgnoreCase))
                Skip = "The temporary folder is not on the Windows volume.";
        }
    }
}
