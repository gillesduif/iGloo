using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianPackageV1(string Name, string Version, string Architecture, long Length, string Sha256);
public sealed record DebianArchiveV1(string Suite, Uri OriginUri, string InReleaseSha256,
    string PackagesSha256, string SigningFingerprint, DateTimeOffset ValidUntilUtc);
// The bundle builder must verify signatures, signed index hashes and the complete APT dependency
// solution. A list of top-level packages or a Live ISO is NOT this authenticated offline bundle.
public sealed record DebianSourceV1(string Release, string Architecture, string BundleRelativePath,
    string KeyringSha256, string DebootstrapSha256, string DependencySolutionSha256,
    ImmutableArray<DebianArchiveV1> Archives, ImmutableArray<DebianPackageV1> Packages, InstallationSourceV1 Artifact)
{
    // Old candidate fixtures may omit this; a native stage dispatcher must reject omission.
    // The embedded set is plan/receipt bound, while independent native verification establishes
    // authenticity and complete dependency closure before any package consumption.
    public OfflineDebianPackageSetV1? OfflinePackageSet { get; init; }
}
public sealed record DebianIdentityV1(string Hostname, string Username, string Locale, string Timezone,
    string Keyboard, string CredentialArtifactSha256);
public sealed record DebianAgentV1(InstallerPayloadManifestV1 Payload, string ServiceRelativePath)
{
    public DebianFirstBootPlanV1? FirstBoot { get; init; }
}
public sealed record DebianDeploymentPlanV1(int SchemaVersion, InstallationOwnershipV1 Ownership,
    RootFileSystemReceiptV1 Root, DebianSourceV1 Source, DebianIdentityV1 Identity, DebianAgentV1 Agent);
public sealed record DebianCommandV1(string Executable, ImmutableArray<string> Arguments);

public static class DebianDeploymentPlanning
{
    public const string StrategyVersion = "debian-target-root-candidate-v1";
    private static readonly string[] RequiredSuites = ["trixie", "trixie-security", "trixie-updates"];
    private static readonly string[] RequiredAgentFiles = ["\\igloo-agent\\agent.py", "\\migration-manifest.json", "\\igloo-agent\\igloo-deployment.service"];

    // This policy declares product intent, not a resolved/pinned dependency closure. Hardware
    // drivers requiring DKMS/MOK, online codecs and migrated application choices still need a
    // separately qualified policy. No arbitrary package names are accepted as executable input.
    public static ImmutableArray<string> WorkstationPackages { get; } =
    [
        "adduser", "ca-certificates", "console-setup", "dbus", "debian-archive-keyring",
        "efibootmgr", "firmware-linux", "firmware-iwlwifi", "firmware-realtek",
        "gdm3", "grub-common", "grub2-common", "grub-efi-amd64", "grub-efi-amd64-signed",
        "initramfs-tools", "keyboard-configuration", "linux-image-amd64", "locales", "mokutil",
        "network-manager", "ntfs-3g", "python3", "python3-gi", "rsync", "shim-signed",
        "shim-signed-common", "sudo", "systemd-sysv", "task-gnome-desktop", "tasksel", "tzdata",
    ];
    // This is a tasksel task, NOT a package named task-standard. Its package selection must
    // be expanded from authenticated Trixie priority/task metadata into the source closure.
    public const string StandardTask = "standard";

    public static Observation<ResolvedInstallationV1> Validate(DebianDeploymentPlanV1 plan,
        Guid generation, Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ValidStructure(plan)) return Observations.Failure<ResolvedInstallationV1>(ObservationAvailability.Ambiguous, "DebianPlanInvalid");
        return InstallationOwnership.ResolveFormattedRoot(plan.Ownership, generation, plan.Root, inventory);
    }

    public static bool ValidStructure(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Agent?.FirstBoot is not null && !DebianFirstBootEvidence.Valid(plan.Agent.FirstBoot)) return false;
        if (plan.SchemaVersion != 1 || plan.Ownership is null || plan.Root is null || plan.Source is null ||
            plan.Identity is null || plan.Agent?.Payload is null || plan.Ownership.Layout is null ||
            plan.Root.GenerationId == Guid.Empty || plan.Root.GenerationId != plan.Ownership.Layout.Plan.GenerationId ||
            plan.Agent.Payload.GenerationId != plan.Root.GenerationId ||
            PreparedStorageOwnership.VerifyStructure(plan.Ownership.Layout).Availability != ObservationAvailability.Available ||
            plan.Root.FileSystem != "EXT4" || plan.Root.FileSystemUuid == Guid.Empty ||
            !plan.Ownership.Layout.StorageOwnership!.CreatedPartitions.Any(p => p.Role == PreparationRole.LinuxRoot && p.Identity == plan.Root.Partition)) return false;
        var source = plan.Source;
        if (source.OfflinePackageSet is not null && OfflineDebianPackageSets.Bind(plan, source.OfflinePackageSet).Availability != ObservationAvailability.Available) return false;
        if (source.Release != "trixie" || source.Architecture != "amd64" || !RelativePath(source.BundleRelativePath) ||
            source.Artifact is null || source.Artifact.Length <= 0 || !RelativePath(source.Artifact.ArtifactName) ||
            !Hash(source.Artifact.Sha256) || !Hash(source.Artifact.PayloadManifestSha256) ||
            !Hash(source.KeyringSha256) || !Hash(source.DebootstrapSha256) || !Hash(source.DependencySolutionSha256) ||
            source.Archives.IsDefaultOrEmpty || source.Packages.IsDefaultOrEmpty ||
            !source.Archives.Select(a => a.Suite).Order(StringComparer.Ordinal).SequenceEqual(RequiredSuites, StringComparer.Ordinal) ||
            source.Archives.Any(a => a.OriginUri is null || !a.OriginUri.IsAbsoluteUri || a.OriginUri.Scheme != "https" ||
                !string.IsNullOrEmpty(a.OriginUri.UserInfo) || !Hash(a.InReleaseSha256) || !Hash(a.PackagesSha256) ||
                a.SigningFingerprint is not { Length: 40 } || !a.SigningFingerprint.All(Uri.IsHexDigit) || a.ValidUntilUtc.Offset != TimeSpan.Zero) ||
            source.Packages.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != source.Packages.Length ||
            source.Packages.Any(p => !DebianWorkstationPolicy.SafePackage(p.Name) || string.IsNullOrWhiteSpace(p.Version) || p.Version.Any(char.IsWhiteSpace) ||
                p.Architecture is not ("amd64" or "all") || p.Length <= 0 || !Hash(p.Sha256)) ||
            WorkstationPackages.Any(p => !source.Packages.Any(v => v.Name == p))) return false;
        var identity = plan.Identity;
        if (!Token(identity.Username) || identity.Username.Length > 32 || identity.Username == "root" ||
            identity.Hostname.Length is < 2 or > 63 || !Token(identity.Hostname) || identity.Hostname.Contains('_', StringComparison.Ordinal) ||
            !SafeConfigurationValue(identity.Locale) || !identity.Locale.EndsWith(".UTF-8", StringComparison.Ordinal) ||
            !RelativePath(identity.Timezone) || !Token(identity.Keyboard) || !Hash(identity.CredentialArtifactSha256)) return false;
        var files = plan.Agent.Payload.Files;
        var payload = plan.Ownership.Esp.Payload.Volume;
        return plan.Agent.ServiceRelativePath == "igloo-agent/igloo-deployment.service" && !files.IsDefaultOrEmpty &&
            files.All(f => f.Role == PreparationRole.Payload && f.File.VolumeGuid == payload.VolumeGuid &&
                f.File.Length > 0 && Hash(f.File.Sha256) && f.File.RelativePath.StartsWith('\\') &&
                RelativePath(f.File.RelativePath[1..].Replace('\\', '/'))) &&
            files.Select(f => f.File.RelativePath.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() == files.Length &&
            RequiredAgentFiles
                .All(p => files.Any(f => string.Equals(f.File.RelativePath, p, StringComparison.OrdinalIgnoreCase)));
    }

    // Plan fingerprint binds EXACT supplied inputs, including ownership/generation/source bytes.
    // It is not RecoverySnapshot's semantic state hash and never replaces structural validation.
    public static string Fingerprint(DebianDeploymentPlanV1 plan)
    {
        if (!ValidStructure(plan)) throw new InvalidDataException("Invalid Debian deployment plan.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
    }

    public static string InstalledAptSources =>
        "Types: deb\nURIs: https://deb.debian.org/debian\nSuites: trixie trixie-updates\n" +
        "Components: main contrib non-free non-free-firmware\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n\n" +
        "Types: deb\nURIs: https://security.debian.org/debian-security\nSuites: trixie-security\n" +
        "Components: main contrib non-free non-free-firmware\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n";

    // Never copy the runtime ID or NM secret_key. Generate identity on first installed boot.
    // Empty machine-id defers generation without requesting ConditionFirstBoot presets.
    public const string IdentityPathRequiredEmpty = "/etc/machine-id";
    public static ImmutableArray<string> IdentityPathsRequiredAbsent { get; } =
    ["/var/lib/NetworkManager/secret_key", "/etc/ssh/ssh_host_*", "/etc/igloo-installer-resolv.conf"];
    public const string DbusMachineIdLink = "/etc/machine-id";
    public const string InstalledResolverLink = "/run/NetworkManager/resolv.conf";
    public const string GrubDefaults = "GRUB_DISTRIBUTOR=Debian\nGRUB_DISABLE_OS_PROBER=true\n";

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Token(string? value) => !string.IsNullOrEmpty(value) && char.IsAsciiLetterLower(value[0]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '+' or '.');
    private static bool SafeConfigurationValue(string? value) => !string.IsNullOrEmpty(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool RelativePath(string? value) => !string.IsNullOrEmpty(value) && value.Split('/').All(p => p.Length > 0 && p is not ("." or "..") &&
        p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+'));
}
