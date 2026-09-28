using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianOfflineFileV1(string Path, long Length, string Sha256);
public sealed record DebianOfflineRepositoryV1(string Id, Uri OriginUri, string Suite,
    DebianOfflineFileV1 InRelease, string ReleaseSha256, string SigningFingerprint,
    ImmutableArray<DebianOfflineFileV1> Indexes);
public sealed record DebianOfflinePackageV1(string RepositoryId, string Name, string Version,
    string Architecture, DebianOfflineFileV1 File);
public sealed record OfflineDebianPackageSetV1(int SchemaVersion, Guid GenerationId, Guid BuildId,
    string Release, string Architecture, string PolicySha256, string KeyringSha256,
    string AptVersion, string DebootstrapSha256, bool InstallRecommends, bool InstallSuggests,
    ImmutableArray<string> BootstrapPackages, ImmutableArray<string> StandardPackages,
    ImmutableArray<DebianOfflineRepositoryV1> Repositories, ImmutableArray<DebianOfflinePackageV1> Packages)
{
    // Base-suite versions used by debootstrap may differ from the final security/updates
    // solution. Retain BOTH archive sets; final dpkg evidence describes only Packages.
    public ImmutableArray<DebianOfflinePackageV1> BootstrapArchives { get; init; } = [];
    // A hash binds independently verified tool bytes/versions; it is not qualification
    // of their ABI, package hooks or effective deployment isolation.
    public string? RuntimeProfileSha256 { get; init; }
    public DebianDependencyClosureReportV1 DependencyPolicy { get; init; } = new(1, [], [], []);
    // Identical package bytes can have different signed pool paths in security/main.
    // Retain both paths so a file-only APT consumer never needs an online fallback.
    public ImmutableArray<DebianOfflinePackageV1> RepositoryAliases { get; init; } = [];
}

// This codec checks identity/structure, NOT cryptographic authenticity or dependency semantics.
// native/offline_bundle.py independently verifies gpgv -> Release -> indexes -> deb bytes,
// then reruns Debian APT's dependency solver. Only that acquisition may report authenticated.
public static class OfflineDebianPackageSets
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] Suites = ["trixie", "trixie-security", "trixie-updates"];

    public static Observation<bool> Validate(OfflineDebianPackageSetV1 set, Guid generation)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.RuntimeProfileSha256 is not null && !DebianDeploymentPlanning.Hash(set.RuntimeProfileSha256))
            return Bad("DebianRuntimeProfileHashInvalid");
        if (set.SchemaVersion != 1 || set.GenerationId != generation || generation == Guid.Empty || set.BuildId == Guid.Empty ||
            set.Release != "trixie" || set.Architecture != "amd64" || set.PolicySha256 != DebianWorkstationPolicy.Fingerprint ||
            !set.InstallRecommends || set.InstallSuggests || !DebianDeploymentPlanning.Hash(set.KeyringSha256) ||
            !DebianDeploymentPlanning.Hash(set.DebootstrapSha256) || string.IsNullOrWhiteSpace(set.AptVersion) ||
            set.Repositories.IsDefaultOrEmpty || set.Packages.IsDefaultOrEmpty || set.BootstrapPackages.IsDefaultOrEmpty ||
            set.StandardPackages.IsDefaultOrEmpty || set.BootstrapArchives.IsDefaultOrEmpty ||
            !set.Repositories.Select(r => r.Suite).Order(StringComparer.Ordinal).SequenceEqual(Suites, StringComparer.Ordinal) ||
            set.Repositories.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != set.Repositories.Length)
            return Bad("DebianOfflinePolicyOrGenerationInvalid");
        foreach (var repo in set.Repositories)
            if (repo.Id != (repo.Suite == "trixie" ? "debian" : repo.Suite == "trixie-updates" ? "debian-updates" : "debian-security") ||
                !Relative(repo.Id) || repo.Id.Contains('/', StringComparison.Ordinal) || !repo.OriginUri.IsAbsoluteUri ||
                repo.OriginUri.Scheme != "https" || repo.OriginUri.UserInfo.Length != 0 || !ValidFile(repo.InRelease) ||
                repo.InRelease.Path != "dists/" + repo.Suite + "/InRelease" ||
                !DebianDeploymentPlanning.Hash(repo.ReleaseSha256) || repo.SigningFingerprint is not { Length: 40 } ||
                !repo.SigningFingerprint.All(Uri.IsHexDigit) || repo.Indexes.IsDefaultOrEmpty ||
                repo.Indexes.Select(i => i.Path).Distinct(StringComparer.Ordinal).Count() != repo.Indexes.Length ||
                repo.Indexes.Any(i => !ValidFile(i) || !i.Path.StartsWith("dists/" + repo.Suite + "/", StringComparison.Ordinal)))
                return Bad("DebianOfflineRepositoryInvalid");
        if (set.Packages.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != set.Packages.Length ||
            set.Packages.Select(p => (p.RepositoryId, p.File.Path)).Distinct().Count() != set.Packages.Length ||
            !set.BootstrapArchives.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(set.BootstrapPackages.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            set.BootstrapArchives.Any(p => p.RepositoryId != "debian") ||
            set.Packages.Concat(set.BootstrapArchives).Any(p => !set.Repositories.Any(r => r.Id == p.RepositoryId) ||
                !DebianWorkstationPolicy.SafePackage(p.Name) || !DebianWorkstationPolicy.SafeVersion(p.Version) ||
                p.Architecture is not ("amd64" or "all") || !ValidFile(p.File) || !p.File.Path.StartsWith("pool/", StringComparison.Ordinal)) ||
            set.BootstrapPackages.Concat(set.StandardPackages).Any(p => !DebianWorkstationPolicy.SafePackage(p)) ||
            DebianWorkstationPolicy.Trixie.Packages.Concat(set.BootstrapPackages).Concat(set.StandardPackages)
                .Any(p => !set.Packages.Any(a => a.Name == p))) return Bad("DebianOfflinePackageSetIncomplete");
        if (!DebianRecommendationPolicy.ValidReport(set)) return Bad("DebianOfflineDependencyPolicyInvalid");
        if (set.RepositoryAliases.IsDefault ||
            set.Packages.Concat(set.RepositoryAliases).Select(p => (p.RepositoryId, p.File.Path)).Distinct().Count() !=
                set.Packages.Length + set.RepositoryAliases.Length ||
            set.RepositoryAliases.Any(a => !set.Repositories.Any(r => r.Id == a.RepositoryId) || !ValidFile(a.File) ||
                !a.File.Path.StartsWith("pool/", StringComparison.Ordinal) || !set.Packages.Any(p =>
                    p.Name == a.Name && p.Version == a.Version && p.Architecture == a.Architecture &&
                    p.File.Length == a.File.Length && p.File.Sha256 == a.File.Sha256)))
            return Bad("DebianOfflineRepositoryAliasInvalid");
        return Observations.Available(true);
    }

    public static byte[] Serialize(OfflineDebianPackageSetV1 set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Require(set, set.GenerationId);
        return JsonSerializer.SerializeToUtf8Bytes(set, Options);
    }

    public static OfflineDebianPackageSetV1 Reopen(ReadOnlySpan<byte> bytes, string expectedSha256, Guid generation)
    {
        if (bytes.Length is <= 0 or > 32 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != expectedSha256)
            throw new InvalidDataException("Offline package manifest integrity mismatch.");
        var set = JsonSerializer.Deserialize<OfflineDebianPackageSetV1>(bytes, Options) ?? throw new InvalidDataException("Missing offline package set.");
        Require(set, generation);
        return set;
    }

    public static Observation<bool> Bind(DebianDeploymentPlanV1 plan, OfflineDebianPackageSetV1 set)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var valid = Validate(set, plan.Root.GenerationId);
        if (valid.Availability != ObservationAvailability.Available) return valid;
        if (set.KeyringSha256 != plan.Source.KeyringSha256 || set.DebootstrapSha256 != plan.Source.DebootstrapSha256 ||
            set.Repositories.Any(r => !plan.Source.Archives.Any(a => a.Suite == r.Suite && a.OriginUri == r.OriginUri &&
                a.InReleaseSha256 == r.InRelease.Sha256 && a.SigningFingerprint == r.SigningFingerprint)) ||
            set.Packages.Length != plan.Source.Packages.Length || set.Packages.Any(p => !plan.Source.Packages.Any(s =>
                s.Name == p.Name && s.Version == p.Version && s.Architecture == p.Architecture && s.Length == p.File.Length && s.Sha256 == p.File.Sha256)))
            return Bad("DebianOfflineSourceBindingMismatch");
        return Observations.Available(true);
    }

    private static void Require(OfflineDebianPackageSetV1 set, Guid generation)
    {
        if (Validate(set, generation).Availability != ObservationAvailability.Available) throw new InvalidDataException("Invalid offline package set.");
    }
    private static bool ValidFile(DebianOfflineFileV1 f) => f is not null && Relative(f.Path) && f.Length > 0 && DebianDeploymentPlanning.Hash(f.Sha256);
    private static bool Relative(string value) => !string.IsNullOrEmpty(value) && value.Split('/').All(p => p.Length > 0 && p is not ("." or "..") &&
        p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or '~' or ':'));
    private static Observation<bool> Bad(string code) => Observations.Failure<bool>(ObservationAvailability.Ambiguous, code);
}
