using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class LabConfiguration
{
    internal static async Task<int> RunAsync(string predecessorDirectory, string continuationPath,
        string runtimePath, string journalsPath, string pinPath, string? derivedAuthorizationPath = null)
    {
        var started = false;
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
            var previous = LabStorageSmoke.Read<InstallerLabStorageEvidenceV1>(Path.Combine(predecessorDirectory, "storage.json"));
            var old = InstallationStorage.VerifyLab(previous, LinuxInstallerInventoryProtocol.Parse(previous.Transitions[^1].Formatted),
                Observations.Available(previous.GuestDisks));
            if (old.Availability != ObservationAvailability.Available) throw new InvalidDataException(old.Code);
            var declaration = LabStorageSmoke.Read<Continuation>(continuationPath);
            var inherited = DebianVerifiedImport.Reopen(old.Value,
                ReadChain(Path.Combine(predecessorDirectory, "session", previous.GenerationId.ToString("D"))),
                ReadChain(Path.Combine(predecessorDirectory, "import", previous.GenerationId.ToString("D"))),
                declaration.PredecessorImportSha256, declaration.PredecessorCloseSha256);
            var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(5));
            var inventory = await LabStorageSmoke.CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
            InstallerLabDerivedAuthorizationV1? authorization = null;
            if (derivedAuthorizationPath is not null)
            {
                authorization = LabStorageSmoke.Read<InstallerLabDerivedAuthorizationV1>(derivedAuthorizationPath);
                if (declaration.Derivation is null || authorization.ImportSha256 != inherited.ResultSha256 ||
                    authorization.CloseSha256 != inherited.CloseSha256 || authorization.OperationId != declaration.OperationId ||
                    authorization.AttemptId != declaration.RunId || authorization.CheckpointSha256 != declaration.CheckpointSha256)
                    throw new InvalidDataException("Derived predecessor binding rejected.");
            }
            else if (declaration.Derivation is not null) throw new InvalidDataException("Explicit derived authorization required.");
            var next = authorization is null ? InstallationStorage.ContinueLabSameTarget(old.Value, declaration.RunId, declaration.OperationId,
                inherited.ResultSha256, declaration.CheckpointSha256, declaration.HostObservationSha256,
                declaration.CreatedBackings, declaration.ReopenedBackings, inventory, LabStorageSmoke.ReadGuest(inventory)) :
                InstallationStorage.ContinueLabCheckpoint(old.Value, authorization, declaration.Derivation!,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(derivedAuthorizationPath!, deadline.Token).ConfigureAwait(false))), declaration.HostObservationSha256,
                    declaration.CreatedBackings, declaration.ReopenedBackings, inventory, LabStorageSmoke.ReadGuest(inventory));
            if (next.Availability != ObservationAvailability.Available) throw new InvalidDataException(next.Code);
            var storage = next.Value;
            var plan = new DebianCoreConfigurationPlanV1(1, declaration.OperationId, storage.Provenance,
                inherited.ResultSha256, inherited.CloseSha256, declaration.CheckpointSha256,
                "igloo-lab-config", "iglootest", 1000, "en_US.UTF-8", "Etc/UTC", "us") { DerivationSha256 = storage.Continuation?.DerivationSha256 };
            var stores = LabStorageSmoke.Read<JournalDeclaration>(journalsPath);
            // One nominated successor per predecessor, independent of the random operation
            // ID. A missing final result never enables another reservation in these stores.
            var suffix = authorization is null ? "/configuration/" + inherited.ResultSha256 :
                "/configuration-derived/" + authorization.AttemptId.ToString("D");
            if (!stores.SessionStore.EndsWith(suffix + "/session", StringComparison.Ordinal) ||
                !stores.ImportStore.EndsWith(suffix + "/effects", StringComparison.Ordinal))
                throw new InvalidDataException("Configuration reservation location changed.");
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage,
                stores.SessionStore, stores.ImportStore, stores.RuntimeFileSystemUuid, deadline.Token);
            var journal = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath, stores.ToolSha256, witnesses[0], runtime);
            var effects = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath, stores.ToolSha256, witnesses[1], runtime);
            var external = LabStorageSmoke.Read<ExternalPin>(pinPath);
            if (external.Use != "DevelopmentImportOnly" || external.ProductionAuthentication != "Unsupported")
                throw new InvalidDataException("Development authentication required.");
            var pin = new DebianRootDevelopmentPinV1(external.BuildId, external.DescriptorSha256, external.PolicySha256, external.NotBeforeUtc, external.NotAfterUtc);
            if (pin.NotBeforeUtc > DateTimeOffset.UtcNow || pin.NotAfterUtc <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("External development approval expired or not active.");
            var authority = DebianMountSessionAuthority.ForLabConfiguration(storage, inherited, plan, journal, effects, pin);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "ConfigurationPreflight", Plan = plan, Predecessor = inherited,
                authority.PlanSha256, authority.SessionId, Stores = witnesses }));
            await using var session = new DebianNativeMountSession(authority);
            started = true;
            await session.StartAsync(runtime, deadline.Token);
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.ConfigureCore,
                DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                var result = await session.PerformAsync(action, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result, authority.LastResultReference, authority.LastResultSha256 }));
            }
            var reopened = await journal.ReopenAsync(authority.LastResultReference!, deadline.Token);
            if (Convert.ToHexString(SHA256.HashData(reopened)) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed)
                throw new IOException("Configuration close reopen failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { Configuration = "AppliedAndVerified", Teardown = "AppliedAndVerified",
                plan.OperationId, authority.SessionId, authority.GenerationId, authority.PlanSha256, authority.LastResultReference,
                authority.LastResultSha256, MachineIdentity = "FirstBootPending", ProductionAuthentication = "Unsupported", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or
            ArgumentException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Configuration = started ? "OutcomeUnknown" : "NotStarted",
                Code = error.Message, ErrorType = error.GetType().Name }));
            return 2;
        }
    }

    internal static ImmutableArray<byte[]> ReadChain(string path)
    {
        DebianSessionToolProtection.Verify(Path.Combine(path, "plan.sha256"));
        var records = ImmutableArray.CreateBuilder<byte[]>();
        var names = Directory.GetFiles(path).Order(StringComparer.Ordinal).ToArray();
        if (names.Length is < 2 or > 20001) throw new InvalidDataException("Predecessor store incomplete.");
        foreach (var file in names)
        {
            DebianSessionToolProtection.Verify(file);
            if (Path.GetFileName(file) == "plan.sha256") continue;
            var bytes = File.ReadAllBytes(file);
            LabStorageSmoke.RequireUnambiguousJson(bytes);
            if (Path.GetFileName(file) != $"{records.Count:D8}-{Convert.ToHexString(SHA256.HashData(bytes))}.json")
                throw new InvalidDataException("Predecessor record name/hash changed.");
            records.Add(bytes);
        }
        using var first = JsonDocument.Parse(records[0]);
        if (File.ReadAllText(Path.Combine(path, "plan.sha256")) != first.RootElement.GetProperty("PlanSha256").GetString())
            throw new InvalidDataException("Predecessor reservation changed.");
        return records.ToImmutable();
    }

    internal readonly record struct Continuation(Guid RunId, Guid OperationId, string CheckpointSha256,
        string PredecessorImportSha256, string PredecessorCloseSha256, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
        InstallerLabDerivationV1? Derivation = null);
    private readonly record struct ExternalPin(string Use, string ProductionAuthentication, Guid BuildId, string DescriptorSha256,
        string PolicySha256, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, string ManifestSha256, string ContentSha256);
}
