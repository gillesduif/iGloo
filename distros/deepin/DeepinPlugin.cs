using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.Deepin;

/// <summary>
/// Catalog metadata and compatibility findings while safe Deepin installation remains
/// unverified. No executable installer configuration or migration payload is exposed.
/// </summary>
public sealed class DeepinPlugin : IDistroPlugin, IInstallationTargetConsumer
{
    private const long MinimumRamBytes = 4L * 1024 * 1024 * 1024;
    private const long RecommendedRamBytes = 8L * 1024 * 1024 * 1024;
    private const string InstallationBlocker =
        "Deepin installation is disabled: a deterministic installation into an explicitly " +
        "identified iGloo root partition, preserving Windows and the existing EFI partition, " +
        "has not been verified. See distros/deepin/STATUS.md.";

    public string Id => "deepin";

    public InstallationTargetRequirement TargetRequirement => InstallationTargetRequirement.CreatedRootAndEsp;

    public DistroMetadata Metadata { get; }

    public DeepinPlugin()
        : this(Path.Join(Path.GetDirectoryName(typeof(DeepinPlugin).Assembly.Location)
            ?? AppContext.BaseDirectory, "distro.json"))
    {
    }

    // An explicit path isolates packaging tests from the installed plugin.
    internal DeepinPlugin(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var raw = JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Deepin distro.json must contain a metadata object.");
        ValidateMetadata(raw);
        Metadata = BuildMetadata(raw);
    }

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Changing the catalog status must never enable this integration.
        var findings = new List<PreflightFinding>
        {
            new(FindingSeverity.Blocker, "DEEPIN_INSTALLATION_UNVERIFIED", InstallationBlocker,
                "Choose a validated distribution until Deepin's installation path is verified."),
        };

        if (report.SecureBootEnabled)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "DEEPIN_SECURE_BOOT",
                "Deepin's installation guide requires Secure Boot to be disabled. " +
                "iGloo has not validated a Deepin Secure Boot installation.",
                "Choose a validated distribution with the Secure Boot support you need."));

        if (report.TotalRamBytes < MinimumRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "DEEPIN_RAM_MINIMUM",
                "Deepin's documented VM test configuration starts at 4 GiB of RAM. This machine " +
                "is below that floor; iGloo has not validated a lower-memory installation.",
                "Use at least 4 GiB of RAM for Deepin VM validation."));
        else if (report.TotalRamBytes < RecommendedRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "DEEPIN_RAM_RECOMMENDED",
                "Deepin recommends at least 8 GiB of RAM. This machine has less.", null));

        if (string.Equals(report.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "DEEPIN_NVIDIA_UNVERIFIED",
                "Deepin's proprietary-driver option and NVIDIA first-boot behavior have not " +
                "been validated by iGloo for this release or GPU.", null));

        return findings;
    }

    public Task<InstallerConfig> RenderInstallerConfigAsync(
        MigrationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(InstallationBlocker);
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Deepin's immutable filesystem and account-creation handoff have not been validated " +
            "for the iGloo agent. No first-boot payload is available. See distros/deepin/STATUS.md.");
    }

    public InstallerBootSpec GetInstallerBootSpec() =>
        throw new NotSupportedException(InstallationBlocker);

    private static void ValidateMetadata(DistroManifest raw)
    {
        if (!string.Equals(raw.Id, "deepin", StringComparison.Ordinal)
            || !string.Equals(raw.InstallerType, "Custom", StringComparison.Ordinal)
            || !string.Equals(raw.DefaultDesktopEnvironment, "DDE", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(raw.DisplayName)
            || string.IsNullOrWhiteSpace(raw.Description))
            throw new InvalidDataException("Deepin metadata must identify deepin, the Custom installer and DDE.");

        var iso = raw.Iso;
        if (iso?.DownloadUrl is not { IsAbsoluteUri: true } url
            || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(url.UserInfo)
            || iso.Sha256 is not { Length: 64 } checksum
            || checksum.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Deepin metadata requires an absolute HTTPS ISO URL and a SHA-256 checksum.");

        if (raw.MinimumRequirements is not { } requirements
            || requirements.MinRamBytes < MinimumRamBytes
            || requirements.MinDiskBytes <= 0
            || !requirements.RequiresUefi
            || !requirements.Requires64Bit)
            throw new InvalidDataException("Deepin metadata requires explicit disk and RAM requirements, UEFI and AMD64.");

        if (raw.Tags is null || raw.Screenshots is null)
            throw new InvalidDataException("Deepin metadata tags and screenshots must be arrays when present.");
    }

    private static DistroMetadata BuildMetadata(DistroManifest raw) => new()
    {
        DisplayName = raw.DisplayName,
        Description = raw.Description,
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment!,
        InstallerType = InstallerType.Custom,
        IsoDownloadUrl = raw.Iso.DownloadUrl,
        IsoSha256 = raw.Iso.Sha256,
        IsoGpgSignatureUrl = raw.Iso.GpgSignatureUrl,
        IsoGpgKeyUrl = raw.Iso.GpgKeyUrl,
        Tags = raw.Tags,
        Screenshots = raw.Screenshots,
        MinimumRequirements = new HardwareRequirements
        {
            MinRamBytes = raw.MinimumRequirements!.MinRamBytes,
            MinDiskBytes = raw.MinimumRequirements.MinDiskBytes,
            RequiresUefi = raw.MinimumRequirements.RequiresUefi,
            Requires64Bit = raw.MinimumRequirements.Requires64Bit,
        },
        Maintainer = raw.Maintainer?.Github,
    };
}
