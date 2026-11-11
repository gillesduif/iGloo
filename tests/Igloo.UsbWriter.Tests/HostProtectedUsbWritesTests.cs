using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.UsbWriter.Tests;

// The protected-host marker stops both USB write routes and every raw write handle.
public sealed class HostProtectedUsbWritesTests
{
    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint ShareReadWrite = 0x00000003u;
    private const uint OpenExisting = 3;

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task Both_usb_write_routes_refuse(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var writer = new UsbWriterService(NullLogger<UsbWriterService>.Instance);
        var drive = new UsbDriveInfo(99, "fixture", 16L << 30, @"\\.\PHYSICALDRIVE99", null, 512);
        var pin = new string('a', 64);

        await Assert.ThrowsAsync<HostProtectedException>(() =>
            writer.WriteAsync(drive, @"C:\absent.iso", @"C:\absent-staging", pin, null));
        await Assert.ThrowsAsync<HostProtectedException>(() =>
            writer.WriteBootableFatSeedAsync(drive, @"C:\absent.iso", @"C:\absent-staging", pin,
                new UsbStagingIdentity(pin, pin),
                new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture" }, null));
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public void A_raw_write_handle_refuses_and_a_read_handle_does_not(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var readable = Path.GetTempFileName();
        try
        {
            // A drive index that does not exist: nothing opens even if the guard were missing.
            Assert.Throws<HostProtectedException>(() => NativeMethods.CreateFileW(@"\\.\PHYSICALDRIVE99",
                GenericRead | GenericWrite, ShareReadWrite, nint.Zero, OpenExisting, 0, nint.Zero));

            using var handle = NativeMethods.CreateFileW(readable, GenericRead, ShareReadWrite, nint.Zero,
                OpenExisting, 0, nint.Zero);
            Assert.False(handle.IsInvalid);
        }
        finally
        {
            File.Delete(readable);
        }
    }
}
