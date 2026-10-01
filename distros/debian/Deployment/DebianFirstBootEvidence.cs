using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianFirstBootState { InstalledFirstBootPending, FirstBootSucceeded, FirstBootFailed, FirstBootOutcomeUnknown }
public sealed record DebianFirstBootRequirementV1(string Kind, string FileName, string Sha256);
public sealed record DebianFirstBootPlanV1(ImmutableArray<DebianFirstBootRequirementV1> RequiredReceipts);
public sealed record DebianFirstBootServiceV1(Guid InvocationId, string UnitName, int ExecMainCode, int ExecMainStatus, string Result);
public sealed record DebianAgentReceiptV1(Guid GenerationId, string Profile, string WorkerSha256,
    string ConfigurationSha256, string UnitSha256, ImmutableArray<DebianAgentFileV1> InstalledFiles,
    bool Enabled, DebianFirstBootState State, string? FirstBootEvidenceSha256)
{
    public DebianFirstBootServiceV1? Service { get; init; }
}

// The worker verifies references, not user-data contents or successful migration. Required
// privileged producers remain separate; a deployment may not omit a required producer to pass.
public static class DebianFirstBootEvidence
{
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public const string Profile = "debian-first-boot-evidence-v1";
    private static readonly string[] CompletionFields = ["SchemaVersion", "GenerationId", "Kind", "State", "EvidenceSha256"];

    public static bool ValidCompletionReceipt(JsonElement receipt, Guid generation, string kind)
    {
        try
        {
            return generation != Guid.Empty && kind is "DeploymentContent" or "UserData" or "Enrollment" &&
                receipt.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(
                    CompletionFields.Order(StringComparer.Ordinal)) &&
                receipt.GetProperty("SchemaVersion").GetInt32() == 1 && receipt.GetProperty("GenerationId").GetGuid() == generation &&
                receipt.GetProperty("Kind").GetString() == kind && receipt.GetProperty("State").GetString() == "AppliedAndVerified" &&
                DebianDeploymentPlanning.Hash(receipt.GetProperty("EvidenceSha256").GetString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
    }

    public static byte[] WorkerBytes()
    {
        using var resource = typeof(DebianFirstBootEvidence).Assembly.GetManifestResourceStream("Igloo.Debian.FirstBootWorker") ??
            throw new InvalidDataException("Missing first-boot worker resource.");
        using var output = new MemoryStream();
        resource.CopyTo(output);
        return output.ToArray();
    }

    public static string WorkerSha256 => Convert.ToHexString(SHA256.HashData(WorkerBytes()));

    public static bool Valid(DebianFirstBootPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return !plan.RequiredReceipts.IsDefaultOrEmpty && plan.RequiredReceipts.All(r => r is not null && r.FileName is not null) &&
            plan.RequiredReceipts.Any(r => r.Kind == "DeploymentContent") &&
            plan.RequiredReceipts.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Count() == plan.RequiredReceipts.Length &&
            plan.RequiredReceipts.Select(r => r.FileName).Distinct(StringComparer.Ordinal).Count() == plan.RequiredReceipts.Length &&
            plan.RequiredReceipts.All(r => r.Kind is "DeploymentContent" or "UserData" or "Enrollment" &&
                r.FileName.EndsWith(".json", StringComparison.Ordinal) && r.FileName.Length > 5 &&
                r.FileName[..^5].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') &&
                DebianDeploymentPlanning.Hash(r.Sha256));
    }

    public static string Configuration(Guid generation, DebianFirstBootPlanV1 plan)
    {
        if (generation == Guid.Empty || !Valid(plan))
            throw new InvalidDataException("Invalid first-boot evidence plan.");
        return JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            GenerationId = generation,
            Profile,
            WorkerSha256,
            RequiredReceipts = plan.RequiredReceipts.OrderBy(r => r.Kind, StringComparer.Ordinal).ToImmutableArray()
        });
    }

    public static Observation<DebianAgentReceiptV1> Installed(DebianAgentInstallProfileV1 profile,
        Observation<DebianAgentReadbackV1> readback)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var verified = DebianAgentProfiles.Verify(profile, readback);
        if (verified.Availability != ObservationAvailability.Available)
            return Observations.Failure<DebianAgentReceiptV1>(verified.Availability, verified.Code ?? "DebianAgentReadbackFailed");
        var worker = profile.Files.Single(f => f.Destination == DebianAgentProfiles.AgentPath);
        if (worker.Sha256 != WorkerSha256 || worker.Length != WorkerBytes().Length)
            return Observations.Failure<DebianAgentReceiptV1>(ObservationAvailability.Unsupported, "LegacyWorkerCannotProduceFirstBootReceipt");
        return Observations.Available(new DebianAgentReceiptV1(profile.GenerationId, Profile, worker.Sha256,
            DebianDeploymentPlanning.TextHash(profile.ConfigurationContent), DebianDeploymentPlanning.TextHash(profile.UnitContent),
            readback.Value.Files, true, DebianFirstBootState.InstalledFirstBootPending, null));
    }

    public static Observation<DebianAgentReceiptV1> ObserveOutcome(DebianAgentReceiptV1 installed,
        DebianFirstBootPlanV1 plan, Observation<ImmutableArray<byte>> observation, Observation<DebianFirstBootServiceV1> service)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(service);
        if (service.Availability != ObservationAvailability.Available)
            return Observations.Failure<DebianAgentReceiptV1>(service.Availability, "FirstBootServiceOutcomeNotObserved");
        if (observation.Availability != ObservationAvailability.Available)
            return Observations.Failure<DebianAgentReceiptV1>(observation.Availability, "FirstBootOutcomeNotObserved");
        if (!Valid(plan) || installed.GenerationId == Guid.Empty || installed.Profile != Profile || !installed.Enabled ||
            installed.WorkerSha256 != WorkerSha256 || !DebianDeploymentPlanning.Hash(installed.ConfigurationSha256) ||
            !DebianDeploymentPlanning.Hash(installed.UnitSha256) || installed.State != DebianFirstBootState.InstalledFirstBootPending ||
            observation.Value.IsDefaultOrEmpty || observation.Value.Length > 1024 * 1024)
            return Observations.Failure<DebianAgentReceiptV1>(ObservationAvailability.Ambiguous, "FirstBootEvidenceInvalid");
        try
        {
            using var document = JsonDocument.Parse(observation.Value.ToArray());
            var root = document.RootElement;
            var expectedNames = new[] { "SchemaVersion", "GenerationId", "Profile", "WorkerSha256", "ConfigurationSha256", "State", "IntentSha256", "VerifiedReceipts", "InvocationId" };
            if (!root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expectedNames.Order(StringComparer.Ordinal)) ||
                root.GetProperty("SchemaVersion").GetInt32() != 1 || root.GetProperty("GenerationId").GetGuid() != installed.GenerationId ||
                root.GetProperty("Profile").GetString() != Profile || root.GetProperty(nameof(WorkerSha256)).GetString() != installed.WorkerSha256 ||
                root.GetProperty("ConfigurationSha256").GetString() != installed.ConfigurationSha256 ||
                !DebianDeploymentPlanning.Hash(root.GetProperty("IntentSha256").GetString()) || service.Value.InvocationId == Guid.Empty ||
                root.GetProperty("InvocationId").GetGuid() != service.Value.InvocationId || service.Value.UnitName != "igloo-deployment.service" ||
                service.Value.ExecMainCode != 1)
                throw new InvalidDataException();
            var state = root.GetProperty("State").GetString() switch
            {
                "FirstBootSucceeded" => DebianFirstBootState.FirstBootSucceeded,
                "FirstBootFailed" => DebianFirstBootState.FirstBootFailed,
                _ => throw new InvalidDataException(),
            };
            var required = JsonSerializer.Deserialize<ImmutableArray<DebianFirstBootRequirementV1>>(root.GetProperty("VerifiedReceipts"), StrictJson);
            if (state == DebianFirstBootState.FirstBootSucceeded
                ? service.Value.ExecMainStatus != 0 || service.Value.Result != "success"
                : service.Value.ExecMainStatus == 0 || service.Value.Result != "exit-code")
                throw new InvalidDataException();
            if (required.IsDefault || required.Any(r => r is null) || (state == DebianFirstBootState.FirstBootSucceeded
                ? !required.OrderBy(r => r.Kind, StringComparer.Ordinal).SequenceEqual(plan.RequiredReceipts.OrderBy(r => r.Kind, StringComparer.Ordinal))
                : !required.IsEmpty))
                throw new InvalidDataException();
            return Observations.Available(installed with
            {
                State = state,
                FirstBootEvidenceSha256 = Convert.ToHexString(SHA256.HashData(observation.Value.AsSpan())),
                Service = service.Value
            });
        }
        catch (JsonException) { return Invalid(); }
        catch (InvalidDataException) { return Invalid(); }
        catch (InvalidOperationException) { return Invalid(); }
        catch (FormatException) { return Invalid(); }
        catch (KeyNotFoundException) { return Invalid(); }
    }

    private static Observation<DebianAgentReceiptV1> Invalid() =>
        Observations.Failure<DebianAgentReceiptV1>(ObservationAvailability.Ambiguous, "FirstBootEvidenceInvalid");
}
