using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Core.Services;

/// <summary>
/// Combines user-supplied setup data, the pre-flight hardware report, and the result of file
/// staging into a <see cref="MigrationManifest"/> that is written to the staging directory.
/// The manifest is the wire format read by the first-boot agent on the freshly-installed system.
/// </summary>
public sealed class ManifestGeneratorService
{
    /// <summary>Builds a <see cref="MigrationManifest"/> from the provided inputs.</summary>
    public MigrationManifest Generate(
        string            distroId,
        UserSetup         userSetup,
        PreflightReport   hardware,
        FileStagingResult staging,
        DiskInfo?         targetDisk = null)
    {
        var browsers = userSetup.SelectedBrowserNames
            .Select(name => new BrowserMigration
            {
                Name               = name,
                ProfileStagingPath = string.Empty,   // M5: copy browser profiles
                IncludesPasswords  = false,
            })
            .ToArray();

        return new MigrationManifest
        {
            DistroId = distroId,

            User = new MigrationUser
            {
                WindowsUsername        = userSetup.WindowsUsername,
                PreferredLinuxUsername = userSetup.LinuxUsername,
                Locale                 = userSetup.Locale,
                Timezone               = userSetup.Timezone,
                Keymap                 = userSetup.Keymap,
            },

            Files = new FileMigrationPlan
            {
                StagingPath     = staging.StagingDirectory,
                TotalBytes      = staging.TotalBytesCopied,
                IncludedFolders = userSetup.SelectedFolderNames,
            },

            Browsers = browsers,

            Hardware = new HardwareProfile
            {
                GpuVendor          = hardware.GpuVendor,
                SecureBootEnabled  = hardware.SecureBootEnabled,
                FirmwareType       = hardware.IsUefi ? "uefi" : "bios",
                NeedsNonFreeCodecs = true,
                TargetDiskModel    = targetDisk?.Model,
                TargetDiskBytes    = targetDisk?.TotalBytes ?? 0,
            },
        };
    }
}

/// <summary>User-supplied migration preferences collected on the Migration Setup wizard step.</summary>
public sealed record UserSetup
{
    public required string                WindowsUsername      { get; init; }
    public required string                LinuxUsername        { get; init; }
    public string                         Locale               { get; init; } = "en_US.UTF-8";
    public string                         Timezone             { get; init; } = "UTC";
    public string                         Keymap               { get; init; } = "us";
    public IReadOnlyList<string>          SelectedFolderNames  { get; init; } = [];
    public IReadOnlyList<string>          SelectedBrowserNames { get; init; } = [];
}
