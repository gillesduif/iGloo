using Igloo.Core.Abstractions;
using Igloo.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

// The protected-host marker stops every service in this assembly that changes disks,
// partitions, boot entries or firmware variables, before it reads or writes anything.
public sealed class HostProtectedWritesTests
{
    // A private vendor namespace: were a guard ever missing, the probe would name nothing that exists.
    private const string ProbeVendor = "{5a1e2c7e-3b0d-4f0b-9c55-1f6c6a0f7d21}";

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task Installer_preparation_and_boot_registration_refuse(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var service = NewDirectInstall();

        await Assert.ThrowsAsync<HostProtectedException>(() => service.PrepareAsync(0, 20L << 30,
            @"C:\absent.iso", @"C:\absent-staging",
            new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture" },
            new InstallerTargetRules(20L << 30, ScansAllDisks: false, UsesExistingEsp: false)));
        await Assert.ThrowsAsync<HostProtectedException>(() => service.RegisterBootEntryAsync());
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task Windows_shrink_refuses(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var resizer = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, new Untouched());

        await Assert.ThrowsAsync<HostProtectedException>(() => resizer.ShrinkAsync(0, 1L << 30));
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public async Task Linux_removal_and_volume_extension_refuse(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);
        var removal = new LinuxRemovalService(NullLogger<LinuxRemovalService>.Instance, new Untouched());

        await Assert.ThrowsAsync<HostProtectedException>(() => removal.RemoveAsync([], [], removingAllLinux: false));
        await Assert.ThrowsAsync<HostProtectedException>(() => removal.ReclaimFreeSpaceAsync(0));
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public void Firmware_variable_writes_refuse(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);

        Assert.Throws<HostProtectedException>(() =>
            FirmwareNative.SetFirmwareEnvironmentVariableW("IglooGuardProbe", ProbeVendor, null, 0));
        Assert.Throws<HostProtectedException>(() =>
            FirmwareNative.SetFirmwareEnvironmentVariableExW("IglooGuardProbe", ProbeVendor, [], 0, 0));
    }

    [Theory]
    [InlineData(HostProtectionState.Protected)]
    [InlineData(HostProtectionState.Unreadable)]
    public void Diskpart_and_boot_store_changes_refuse(HostProtectionState state)
    {
        using var scope = HostProtection.UseForCurrentScope(state);

        // Arguments chosen to change nothing even if a guard were missing.
        Assert.Throws<HostProtectedException>(() => DirectInstallService.RunDiskpart("exit"));
        Assert.Throws<HostProtectedException>(() => NewDirectInstall().RunBcdedit("/?"));
        Assert.Throws<HostProtectedException>(() => new WindowsBcdMutationWriter().Delete(Guid.NewGuid()));
    }

    private static DirectInstallService NewDirectInstall() => new(new Untouched(),
        NullLogger<DirectInstallService>.Instance, new Untouched(), new Untouched(), new Untouched());

    private sealed class Untouched : IPartitionResizeService, IWindowsStorageReader, IWindowsBcdReader,
        IWindowsFirmwareReader
    {
        private static InvalidOperationException Touched() => new("The refusal must come before any read or write.");

        public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default) => throw Touched();
        public Task ShrinkAsync(int diskNumber, long linuxSizeBytes, IProgress<string>? progress = null,
            CancellationToken ct = default) => throw Touched();
        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot() => throw Touched();
        public WindowsStorageBatch ReadDisks() => throw Touched();
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null,
            bool efiOnly = false) => throw Touched();
        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw Touched();
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) => throw Touched();
        public BcdListingObservation ReadFirmware() => throw Touched();
        public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => throw Touched();
        public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096) => throw Touched();
    }
}
