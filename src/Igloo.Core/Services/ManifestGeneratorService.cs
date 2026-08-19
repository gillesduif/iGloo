using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Core.Services;

public static class ManifestGeneratorService
{
    
    public static MigrationManifest Generate(
        string distroId,
        UserSetup userSetup,
        PreflightReport hardware,
        FileStagingResult staging,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.ReplaceDisk,
        int linuxSizeGb = 0)
    {
        ArgumentNullException.ThrowIfNull(userSetup);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(staging);

        // Prefer the richly-resolved browser list (engine + source/dest paths) built on the
        // Windows side. Fall back to bare names for back-compat when only names are supplied.
        var browsers = userSetup.SelectedBrowsers.Count > 0
            ? userSetup.SelectedBrowsers
            : userSetup.SelectedBrowserNames
                .Select(name => new BrowserMigration
                {
                    Name = name,
                    ProfileStagingPath = string.Empty,
                    IncludesPasswords = false,
                })
                .ToArray();

        return new MigrationManifest
        {
            DistroId = distroId,

            User = new MigrationUser
            {
                WindowsUsername = userSetup.WindowsUsername,
                PreferredLinuxUsername = userSetup.LinuxUsername,
                Locale = userSetup.Locale,
                Timezone = userSetup.Timezone,
                Keymap = userSetup.Keymap,
                LinuxPasswordCrypted = LinuxPasswordHasher.Sha512Crypt(userSetup.LinuxPassword),
            },

            Files = new FileMigrationPlan
            {
                StagingPath = staging.StagingDirectory,
                TotalBytes = staging.TotalBytesCopied,
                IncludedFolders = userSetup.SelectedFolderNames,
                Folders = userSetup.SelectedFolders,
            },

            Browsers = browsers,

            SuggestedPackages = userSetup.SuggestedPackages,

            WifiNetworks = userSetup.WifiNetworks,

            Hardware = new HardwareProfile
            {
                GpuVendor = hardware.GpuVendor,
                SecureBootEnabled = hardware.SecureBootEnabled,
                FirmwareType = hardware.IsUefi ? "uefi" : "bios",
                NeedsNonFreeCodecs = true,
                TargetDiskModel = targetDisk?.Model,
                TargetDiskBytes = targetDisk?.TotalBytes ?? 0,
                InstallMode = installMode == DiskInstallMode.DualBoot ? "dual-boot" : "replace",
                LinuxPartitionSizeGb = installMode == DiskInstallMode.DualBoot ? linuxSizeGb : 0,
            },

            Displays = [.. hardware.Displays.Select(d => new DisplayLayout
            {
                PnpId = d.PnpId,
                WidthPx = d.WidthPx,
                HeightPx = d.HeightPx,
                RefreshHz = d.RefreshHz,
                RotationDegrees = d.RotationDegrees,
                PositionX = d.PositionX,
                PositionY = d.PositionY,
                ScalePercent = d.ScalePercent,
                IsPrimary = d.IsPrimary,
            })],
        };
    }
}


public sealed record UserSetup
{
    public required string WindowsUsername { get; init; }
    public required string LinuxUsername { get; init; }
    public string? LinuxPassword { get; init; }
    public string Locale { get; init; } = "en_US.UTF-8";
    public string Timezone { get; init; } = "UTC";
    public string Keymap { get; init; } = "us";
    public IReadOnlyList<string> SelectedFolderNames { get; init; } = [];
    public IReadOnlyList<MigrationFolder> SelectedFolders { get; init; } = [];
    public IReadOnlyList<string> SelectedBrowserNames { get; init; } = [];
    public IReadOnlyList<BrowserMigration> SelectedBrowsers { get; init; } = [];
    public IReadOnlyList<SuggestedPackage> SuggestedPackages { get; init; } = [];
    public IReadOnlyList<WifiNetwork> WifiNetworks { get; init; } = [];
}
