using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianAgentCompletion { NotInstalled, InstalledFirstBootPending, FirstBootVerified, Failed, OutcomeUnknown }
public sealed record DebianAgentFileV1(string Source, string Destination, long Length, string Sha256, uint Owner, uint Group, int Mode);
public sealed record DebianAgentInstallProfileV1(string ProfileVersion, Guid GenerationId,
    ImmutableArray<DebianAgentFileV1> Files, string ConfigurationContent, string UnitContent, string EnableLink, string EnableTarget,
    bool StartDuringDeployment);
public sealed record DebianAgentReadbackV1(Guid GenerationId, ImmutableArray<DebianAgentFileV1> Files,
    string UnitContent, string EnableLink, string EnableTarget, bool StartedDuringDeployment);

public static class DebianAgentProfiles
{
    public const string Version = "debian-additive-agent-v1";
    public const string AgentPath = "/usr/lib/igloo/debian-agent.py";
    public const string ConfigurationPath = "/etc/igloo/deployment.json";
    public const string UnitPath = "/etc/systemd/system/igloo-deployment.service";
    public const string Unit = "[Unit]\nDescription=iGloo explicit deployment agent\nAfter=local-fs.target\n" +
        "[Service]\nType=oneshot\nExecStart=/usr/bin/python3 -I /usr/lib/igloo/debian-agent.py --config /etc/igloo/deployment.json\n" +
        "DynamicUser=yes\nUser=igloo-deployment\nStateDirectory=igloo-deployment\nStateDirectoryMode=0700\n" +
        "NoNewPrivileges=yes\nPrivateDevices=yes\nPrivateNetwork=yes\nProtectSystem=strict\nProtectHome=yes\n" +
        "ProtectKernelTunables=yes\nProtectKernelModules=yes\nProtectControlGroups=yes\n" +
        "CapabilityBoundingSet=\nRestrictSUIDSGID=yes\nRestrictNamespaces=yes\n" +
        "RestrictAddressFamilies=AF_UNIX\nSystemCallFilter=~@mount @raw-io @reboot\n" +
        "InaccessiblePaths=/boot /sys/firmware\nUMask=0077\n" +
        "[Install]\nWantedBy=multi-user.target\n";

    public static DebianAgentInstallProfileV1 Declare(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = DebianDeploymentPlanning.Fingerprint(plan);
        var sources = plan.Agent.Payload.Files;
        // Raw migration manifests can contain credentials and broad legacy actions. This
        // profile never copies one into the new service. Future exact import/enrollment work
        // requires a separately reviewed, secret-free profile rather than executing that input.
        var config = plan.Agent.FirstBoot is not null ? DebianFirstBootEvidence.Configuration(plan.Root.GenerationId, plan.Agent.FirstBoot) :
            System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1,
                plan.Root.GenerationId, Profile = Version, Completion = "FirstBootPending" });
        DebianAgentFileV1 Map(string source, string destination, int mode)
        {
            var file = sources.Single(p => string.Equals(p.File.RelativePath, source, StringComparison.OrdinalIgnoreCase)).File;
            return new(source, destination, file.Length, file.Sha256, 0, 0, mode);
        }
        return new(Version, plan.Root.GenerationId,
            [Map("\\igloo-agent\\agent.py", AgentPath, 0x1a4),
             new("generated:" + Version, ConfigurationPath, System.Text.Encoding.UTF8.GetByteCount(config), DebianDeploymentPlanning.TextHash(config), 0, 0, 0x1a4),
             new("generated:" + Version, UnitPath, System.Text.Encoding.UTF8.GetByteCount(Unit), DebianDeploymentPlanning.TextHash(Unit), 0, 0, 0x1a4)],
            config, Unit, "/etc/systemd/system/multi-user.target.wants/igloo-deployment.service", UnitPath, false);
    }

    public static Observation<DebianAgentCompletion> Verify(DebianAgentInstallProfileV1 expected,
        Observation<DebianAgentReadbackV1> observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Availability != ObservationAvailability.Available)
            return Observations.Failure<DebianAgentCompletion>(observed.Availability, "DebianAgentReadbackUnavailable");
        var actual = observed.Value;
        if (expected.ProfileVersion != Version || actual.GenerationId != expected.GenerationId || actual.Files.IsDefault ||
            !actual.Files.OrderBy(f => f.Destination, StringComparer.Ordinal).SequenceEqual(expected.Files.OrderBy(f => f.Destination, StringComparer.Ordinal)) ||
            actual.UnitContent != Unit || actual.EnableLink != expected.EnableLink || actual.EnableTarget != UnitPath || actual.StartedDuringDeployment)
            return Observations.Failure<DebianAgentCompletion>(ObservationAvailability.Ambiguous, "DebianAgentDestinationOrContentChanged");
        return Observations.Available(DebianAgentCompletion.InstalledFirstBootPending);
    }

    // The legacy payload is not certified by matching a filename/hash. No replacement migration
    // worker is qualified yet; this exact additive installation profile must not execute it.
    public static Observation<bool> WorkerQualification => Observations.Failure<bool>(ObservationAvailability.Unsupported,
        "DebianPrivilegedUserDataCompletionProducerNotQualified");

    public static Observation<bool> VerifyWorkerPayload(DebianDeploymentPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Agent.FirstBoot is null || !DebianFirstBootEvidence.Valid(plan.Agent.FirstBoot))
            return Observations.Failure<bool>(ObservationAvailability.Unsupported, "DebianFirstBootRequirementsMissing");
        var profile = Declare(plan);
        var worker = profile.Files.Single(f => f.Destination == AgentPath);
        return worker.Sha256 == DebianFirstBootEvidence.WorkerSha256 && worker.Length == DebianFirstBootEvidence.WorkerBytes().Length
            ? Observations.Available(true)
            : Observations.Failure<bool>(ObservationAvailability.Ambiguous, "DebianFirstBootWorkerPayloadMismatch");
    }
}
