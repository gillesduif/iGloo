using System.Collections.Immutable;
using System.Text.Json;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianWorkstationPolicyV1(int SchemaVersion, string Release, string Architecture,
    bool InstallRecommends, bool InstallSuggests, ImmutableArray<string> Packages,
    ImmutableArray<string> Tasks, ImmutableArray<string> FirmwarePackages,
    ImmutableArray<string> FirstBootResponsibilities)
{
    public string Revision { get; init; } = "trixie-workstation-2026-09-28.1";
    public ImmutableArray<DebianRecommendationExceptionV1> RecommendationExceptions { get; init; } = [];
}

// Product intent, distinct from an APT-resolved closure. No task name is an APT package.
public static class DebianWorkstationPolicy
{
    public static DebianWorkstationPolicyV1 Trixie { get; } = new(1, "trixie", "amd64", true, false,
        DebianDeploymentPlanning.WorkstationPackages, ["standard"],
        ["firmware-linux", "firmware-iwlwifi", "firmware-realtek"],
        ["GenerateSystemMachineIdentity", "NetworkManagerConnectivity", "MigrationAgentCompletion",
         "OptionalOnlineApplicationsAndCodecs"])
    { RecommendationExceptions = [DebianRecommendationPolicy.Wsdd] };

    public static string Fingerprint => DebianDeploymentPlanning.TextHash(JsonSerializer.Serialize(Trixie));

    // The bundle resolver expands these priorities from SIGNED candidate package records.
    // debootstrap's --print-debs minbase result is also required; the base is not hand guessed.
    public static ImmutableArray<string> StandardPriorities { get; } = ["required", "important", "standard"];

    public static DebianCommandV1 InstallPinned(ImmutableArray<DebianPackageV1> packages)
    {
        if (packages.IsDefaultOrEmpty || packages.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != packages.Length ||
            packages.Any(p => !SafePackage(p.Name) || !SafeVersion(p.Version) || p.Architecture is not ("amd64" or "all")))
            throw new InvalidDataException("Invalid pinned Debian package operation.");
        // The namespace denies networking. Explicit file-only sources replace, rather than
        // supplement, installed online sources. No ignored failures or unauthenticated fallback.
        return new("/usr/bin/apt-get", ["-o", "Dir::Etc::sourcelist=/etc/apt/igloo-offline.sources",
            "-o", "Dir::Etc::sourceparts=-", "-o", "APT::Install-Recommends=true",
            "-o", "APT::Install-Suggests=false", "-o", "Acquire::Retries=0",
            "--assume-yes", "--no-remove", "install",
            .. packages.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + (p.Architecture == "all" ? "" : ":amd64") + "=" + p.Version)]);
    }

    // Debian Policy 5.6.1/5.6.7 permits a leading digit (the real closure includes 7zip).
    internal static bool SafePackage(string value) => value is { Length: >= 2 } &&
        (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.');
    internal static bool SafeVersion(string value) => !string.IsNullOrEmpty(value) && char.IsAsciiDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or ':' or '~');
}
