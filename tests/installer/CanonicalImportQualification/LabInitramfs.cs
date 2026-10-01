using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class LabInitramfs
{
    internal static (ValidatedInstallationStorage Storage, DebianVerifiedConfiguration Verified) Reopen(string directory)
    {
        var evidence = LabStorageSmoke.Read<InstallerLabStorageEvidenceV1>(Path.Combine(directory, "import", "storage.json"));
        var importedStorage = InstallationStorage.VerifyLab(evidence,
            LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted), Observations.Available(evidence.GuestDisks));
        if (importedStorage.Availability != ObservationAvailability.Available) throw new InvalidDataException(importedStorage.Code);
        var previous = LabStorageSmoke.Read<LabConfiguration.Continuation>(Path.Combine(directory, "continuation.json"));
        var imported = DebianVerifiedImport.Reopen(importedStorage.Value,
            LabConfiguration.ReadChain(Path.Combine(directory, "import", "session", evidence.GenerationId.ToString("D"))),
            LabConfiguration.ReadChain(Path.Combine(directory, "import", "import", evidence.GenerationId.ToString("D"))),
            previous.PredecessorImportSha256, previous.PredecessorCloseSha256);
        var authorizationPath = Path.Combine(directory, "derived-authorization.json");
        var authorization = LabStorageSmoke.Read<InstallerLabDerivedAuthorizationV1>(authorizationPath);
        if (authorization.ImportSha256 != imported.ResultSha256 || authorization.CloseSha256 != imported.CloseSha256 ||
            previous.Derivation is null) throw new InvalidDataException("Configured import lineage differs.");
        var inventoryPath = Path.Combine(directory, "continuation-inventory.json");
        DebianSessionToolProtection.Verify(inventoryPath);
        var inventory = LinuxInstallerInventoryProtocol.Parse(File.ReadAllText(inventoryPath));
        // Historical reopening is not a current acquisition witness. Current
        // RunAsync still observes the live complete inventory and guest topology.
        var configured = InstallationStorage.ContinueLabCheckpoint(importedStorage.Value, authorization, previous.Derivation,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(authorizationPath))), previous.HostObservationSha256,
            previous.CreatedBackings, previous.ReopenedBackings, inventory, Observations.Available(evidence.GuestDisks));
        if (configured.Availability != ObservationAvailability.Available) throw new InvalidDataException(configured.Code);
        var plan = LabStorageSmoke.Read<DebianCoreConfigurationPlanV1>(Path.Combine(directory, "configuration-plan.json"));
        var binding = LabStorageSmoke.Read<PredecessorBinding>(Path.Combine(directory, "binding.json"));
        var verified = DebianVerifiedConfiguration.Reopen(configured.Value, imported, plan,
            LabConfiguration.ReadChain(Path.Combine(directory, "session", evidence.GenerationId.ToString("D"))),
            LabConfiguration.ReadChain(Path.Combine(directory, "effects", evidence.GenerationId.ToString("D"))),
            binding.ConfigurationSha256, binding.CloseSha256);
        return (configured.Value, verified);
    }

    internal static async Task<int> RunAsync(string predecessorPath, string declarationPath, string authorizationPath,
        string runtimePath, string journalPath, string pinPath, string candidatePath)
    {
        var invoked = false;
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
            var (previousStorage, previous) = Reopen(predecessorPath);
            var declaration = LabStorageSmoke.Read<Declaration>(declarationPath);
            var authorization = LabStorageSmoke.Read<InstallerLabInitramfsAuthorizationV1>(authorizationPath);
            if (declaration.Derivation.Authorization != authorization || authorization.ConfigurationSha256 != previous.ResultSha256 ||
                authorization.CloseSha256 != previous.CloseSha256 || declaration.OperationId != authorization.OperationId ||
                declaration.RunId != authorization.AttemptId)
                throw new InvalidDataException("Initramfs operation/predecessor binding rejected.");
            var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(5));
            var inventory = await LabStorageSmoke.CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
            var derived = InstallationStorage.ContinueLabConfiguredCheckpoint(previousStorage, declaration.Derivation,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(authorizationPath, deadline.Token).ConfigureAwait(false))), declaration.HostObservationSha256,
                declaration.CreatedBackings, declaration.ReopenedBackings, inventory, LabStorageSmoke.ReadGuest(inventory));
            if (derived.Availability != ObservationAvailability.Available) throw new InvalidDataException(derived.Code);
            var storage = derived.Value;
            var candidate = LabStorageSmoke.Read<JsonElement>(candidatePath);
            var plan = new DebianLabInitramfsPlanV1(1, authorization.OperationId, storage.Provenance,
                previous.ResultSha256, previous.CloseSha256, authorization.RetentionSha256, storage.Continuation!.DerivationSha256!,
                previous.Observation.GetProperty("FilesystemDeltaSha256").GetString()!, candidate,
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtime.ToolHashes))));
            _ = plan.Fingerprint(storage, previous);
            var stores = LabStorageSmoke.Read<JournalDeclaration>(journalPath);
            var parent = "/lab-journal/initramfs/" + authorization.AttemptId.ToString("D");
            if (stores.SessionStore != parent + "/session" || stores.ImportStore != parent + "/effects")
                throw new InvalidDataException("Initramfs journal reservation location differs.");
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage,
                stores.SessionStore, stores.ImportStore, stores.RuntimeFileSystemUuid, deadline.Token);
            var sessionStore = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath, stores.ToolSha256, witnesses[0], runtime);
            var effectStore = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath, stores.ToolSha256, witnesses[1], runtime);
            var external = LabStorageSmoke.Read<ExternalPin>(pinPath);
            if (external.Use != "DevelopmentImportOnly" || external.ProductionAuthentication != "Unsupported")
                throw new InvalidDataException("External development authentication required.");
            var pin = new DebianRootDevelopmentPinV1(external.BuildId, external.DescriptorSha256, external.PolicySha256,
                external.NotBeforeUtc, external.NotAfterUtc);
            var authority = DebianMountSessionAuthority.ForLabInitramfs(storage, previous, plan, sessionStore, effectStore, pin);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "InitramfsPreflight", Plan = plan,
                authority.PlanSha256, authority.SessionId, Stores = witnesses }));
            await using var session = new DebianNativeMountSession(authority);
            invoked = true;
            await session.StartAsync(runtime, deadline.Token);
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.GenerateInitramfs,
                DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                var result = await session.PerformAsync(action, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result, authority.LastResultReference, authority.LastResultSha256 }));
            }
            var close = await sessionStore.ReopenAsync(authority.LastResultReference!, deadline.Token);
            if (Convert.ToHexString(SHA256.HashData(close)) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed)
                throw new IOException("Initramfs Close reopen failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { Initramfs = "AppliedAndVerified", Teardown = "AppliedAndVerified",
                plan.OperationId, authority.SessionId, authority.PlanSha256, authority.LastResultReference, authority.LastResultSha256,
                ProductionAuthentication = "Unsupported", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or
            ArgumentException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Initramfs = invoked ? "OutcomeUnknown" : "NotStarted",
                Code = error.Message, ErrorType = error.GetType().Name }));
            return 2;
        }
    }

    private readonly record struct PredecessorBinding(string ConfigurationSha256, string CloseSha256);
    private readonly record struct Declaration(Guid RunId, Guid OperationId, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
        InstallerLabInitramfsDerivationV1 Derivation);
    private readonly record struct ExternalPin(string Use, string ProductionAuthentication, Guid BuildId, string DescriptorSha256,
        string PolicySha256, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, string ManifestSha256, string ContentSha256);
}
