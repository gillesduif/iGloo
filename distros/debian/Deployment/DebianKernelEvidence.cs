using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianBootArtifactV1(string Path, long Length, string Sha256, bool RegularFile, bool ContainsSymlink);
public sealed record DebianKernelReadbackV1(Guid GenerationId, string KernelRelease, DebianInstalledPackageV1 Package,
    DebianBootArtifactV1 Kernel, DebianBootArtifactV1 ModulesDependencyIndex, DebianBootArtifactV1? Initramfs,
    ImmutableArray<string> InitramfsKernelReleases);

public static class DebianKernelEvidence
{
    public static Observation<bool> Verify(DebianDeploymentPlanV1 plan, Observation<DebianKernelReadbackV1> observed,
        bool requireInitramfs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Availability != ObservationAvailability.Available)
            return Observations.Failure<bool>(observed.Availability, "DebianKernelReadbackUnavailable");
        var value = observed.Value;
        var release = value.KernelRelease;
        if (value.GenerationId != plan.Root.GenerationId || string.IsNullOrEmpty(release) ||
            !release.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '+' or '_') ||
            value.Package.Name != "linux-image-" + release || value.Package.DpkgStatus != "install ok installed" ||
            !plan.Source.Packages.Any(p => p.Name == value.Package.Name && p.Version == value.Package.Version && p.Architecture == value.Package.Architecture) ||
            // Trixie's merged-/usr layout makes /lib a symlink. Observe the canonical
            // modules destination, rather than accidentally accepting an arbitrary alias.
            !Valid(value.Kernel, "/boot/vmlinuz-" + release) || !Valid(value.ModulesDependencyIndex, "/usr/lib/modules/" + release + "/modules.dep") ||
            (requireInitramfs && (!Valid(value.Initramfs, "/boot/initrd.img-" + release) ||
                value.InitramfsKernelReleases.IsDefaultOrEmpty || !value.InitramfsKernelReleases.All(r => r == release))))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "DebianKernelArtifactsIncompleteOrChanged");
        return Observations.Available(true);
    }

    private static bool Valid(DebianBootArtifactV1? file, string path) => file is not null && file.Path == path &&
        file.Length > 0 && DebianDeploymentPlanning.Hash(file.Sha256) && file.RegularFile && !file.ContainsSymlink;
}
