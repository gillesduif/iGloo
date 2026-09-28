using System.Collections.Immutable;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianConfigurationKind { Utf8File, SymbolicLink, MustBeAbsent }
public sealed record DebianConfigurationV1(string Path, DebianConfigurationKind Kind, string? ContentOrTarget, int UnixMode);
public sealed record DebianAccountPolicyV1(string Username, uint UserId, string CredentialArtifactSha256,
    ImmutableArray<string> SupplementaryGroups, bool RootLoginLocked);

// Pure outputs for the exact target root. They neither write files nor resolve symlinks. Native
// application must use the mount boundary and independently verify type/content/mode/ownership.
public static class DebianTargetConfiguration
{
    public static ImmutableArray<DebianConfigurationV1> Generate(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = DebianDeploymentPlanning.Fingerprint(plan);
        var identity = plan.Identity;
        return
        [
            File("/etc/hostname", identity.Hostname + "\n"),
            File("/etc/hosts", "127.0.0.1 localhost\n127.0.1.1 " + identity.Hostname + "\n::1 localhost ip6-localhost ip6-loopback\n"),
            File("/etc/locale.gen", identity.Locale + " UTF-8\n"),
            File("/etc/default/locale", "LANG=" + identity.Locale + "\n"),
            Link("/etc/localtime", "/usr/share/zoneinfo/" + identity.Timezone),
            File("/etc/default/keyboard", "XKBMODEL=\"pc105\"\nXKBLAYOUT=\"" + identity.Keyboard + "\"\nXKBVARIANT=\"\"\nXKBOPTIONS=\"\"\n"),
            File("/etc/network/interfaces", "auto lo\niface lo inet loopback\n"),
            Link("/etc/resolv.conf", DebianDeploymentPlanning.InstalledResolverLink),
            // systemd machine-id(5): an empty image file permits early read-only /etc
            // initialization without ConditionFirstBoot presetting the enabled worker.
            File("/etc/machine-id", ""),
            Link("/var/lib/dbus/machine-id", DebianDeploymentPlanning.DbusMachineIdLink),
            Absent("/var/lib/NetworkManager/secret_key"),
            File("/etc/apt/sources.list.d/debian.sources", DebianDeploymentPlanning.InstalledAptSources),
            Absent("/etc/apt/sources.list"),
            File("/etc/default/grub.d/60-igloo.cfg", DebianDeploymentPlanning.GrubDefaults),
            // Final installed state; these temporary policy/transport files MUST be removed and
            // absence verified. Never inherit the install runtime's resolver or service sockets.
            Absent("/usr/sbin/policy-rc.d"),
            Absent("/etc/apt/igloo-offline.sources"),
            Absent("/etc/igloo-installer-resolv.conf"),
        ];
    }

    public static DebianAccountPolicyV1 Accounts(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = DebianDeploymentPlanning.Fingerprint(plan);
        // UID and username must both be absent in the fresh target before creation. Password
        // material stays in a separate protected artifact, never command arguments or receipts.
        return new(plan.Identity.Username, 1000, plan.Identity.CredentialArtifactSha256, ["sudo"], true);
    }

    public const string TemporaryServicePolicy = "#!/bin/sh\nexit 101\n";
    public static string OfflineAptSources(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = DebianDeploymentPlanning.Fingerprint(plan);
        var root = "file:/run/igloo-source/" + plan.Source.BundleRelativePath;
        return "Types: deb\nURIs: " + root + "/debian\nSuites: trixie\n" +
            "Components: main contrib non-free non-free-firmware\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n\n" +
            "Types: deb\nURIs: " + root + "/debian-updates\nSuites: trixie-updates\n" +
            "Components: main contrib non-free non-free-firmware\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n\n" +
            "Types: deb\nURIs: " + root + "/debian-security\nSuites: trixie-security\n" +
            "Components: main contrib non-free non-free-firmware\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n";
    }

    private static DebianConfigurationV1 File(string path, string text) => new(path, DebianConfigurationKind.Utf8File, text, 0x1a4); // 0644
    private static DebianConfigurationV1 Link(string path, string target) => new(path, DebianConfigurationKind.SymbolicLink, target, 0x1ff); // 0777
    private static DebianConfigurationV1 Absent(string path) => new(path, DebianConfigurationKind.MustBeAbsent, null, 0);
}
