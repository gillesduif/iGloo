using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class WindowsVolumeSnapshotsTests
{
    private const string Device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7";

    [Theory]
    [InlineData(@"C:\Users\ada\AppData\Local\Microsoft\Edge\User Data", @"C:\")]
    [InlineData(@"d:\profiles\ada", @"D:\")]
    [InlineData(@"\\server\share\ada", null)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Users", null)]
    public void Only_a_lettered_local_volume_gets_a_snapshot(string path, string? root) =>
        Assert.Equal(root, WindowsVolumeSnapshots.VolumeRoot(path));

    [Fact]
    public void A_path_keeps_its_place_below_the_snapshot_device()
    {
        Assert.Equal(Device + @"\Users\ada\AppData\Local\Microsoft\Edge\User Data\Default\History",
            WindowsVolumeSnapshots.MapPath(@"C:\", Device,
                @"c:\Users\ada\AppData\Local\Microsoft\Edge\User Data\Default\History"));
        Assert.Equal(Device + @"\Users\ada", WindowsVolumeSnapshots.MapPath(@"C:\", Device + @"\", @"C:\Users\ada"));
    }

    [Fact]
    public void A_path_on_another_volume_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WindowsVolumeSnapshots.MapPath(@"C:\", Device, @"D:\Users\ada"));
    }
}
