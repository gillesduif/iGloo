using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class LabUserData
{
    internal static (ValidatedInstallationStorage Storage, DebianVerifiedInitramfs Verified) Reopen(string directory)
    {
        var (configuredStorage, configured) = LabInitramfs.Reopen(Path.Combine(directory, "configured"));
        var authPath = Path.Combine(directory, "initramfs-authorization.json");
        var auth = LabStorageSmoke.Read<InstallerLabInitramfsAuthorizationV1>(authPath);
        var declaration = LabStorageSmoke.Read<InitramfsDeclaration>(Path.Combine(directory, "declaration.json"));
        var inventoryPath = Path.Combine(directory, "inventory.json"); DebianSessionToolProtection.Verify(inventoryPath);
        var inventory = LinuxInstallerInventoryProtocol.Parse(File.ReadAllText(inventoryPath));
        if (auth != declaration.Derivation.Authorization || auth.ConfigurationSha256 != configured.ResultSha256 ||
            auth.CloseSha256 != configured.CloseSha256 || auth.AttemptId != declaration.RunId || auth.OperationId != declaration.OperationId)
            throw new InvalidDataException("Initramfs predecessor declaration changed.");
        // Reopen historical evidence against its recorded guest observation. The
        // active continuation below separately requires fresh live guest witnesses.
        var historical = LabStorageSmoke.Read<InstallerLabStorageEvidenceV1>(Path.Combine(directory, "configured", "import", "storage.json"));
        var storage = InstallationStorage.ContinueLabConfiguredCheckpoint(configuredStorage, declaration.Derivation,
            Hash(File.ReadAllBytes(authPath)), declaration.HostObservationSha256, declaration.CreatedBackings,
            declaration.ReopenedBackings, inventory, Observations.Available(historical.GuestDisks));
        if (storage.Availability != ObservationAvailability.Available) throw new InvalidDataException(storage.Code);
        var plan = LabStorageSmoke.Read<DebianLabInitramfsPlanV1>(Path.Combine(directory, "initramfs-plan.json"));
        var binding = LabStorageSmoke.Read<Binding>(Path.Combine(directory, "binding.json"));
        var generation = storage.Value.Provenance.GenerationId.ToString("D");
        return (storage.Value, DebianVerifiedInitramfs.Reopen(storage.Value, configured, plan,
            LabConfiguration.ReadChain(Path.Combine(directory, "session", generation)),
            LabConfiguration.ReadChain(Path.Combine(directory, "effects", generation)), binding.InitramfsSha256, binding.CloseSha256));
    }

    private static DebianRootDevelopmentPinV1 ReadPin(string path, DebianVerifiedInitramfs previous)
    {
        var pin = LabStorageSmoke.Read<UserDataExternalPin>(path);
        var imported = previous.Configured.Imported;
        return pin.Authenticate(imported.BuildId, imported.DescriptorSha256, imported.ManifestSha256,
            imported.ContentSha256, DateTimeOffset.UtcNow);
    }

    private static (Declaration Declaration, InstallerLabUserDataAuthorizationV1 Authorization) ReadOperation(
        string declarationPath, string authorizationPath, DebianVerifiedInitramfs previous)
    {
        var declaration = LabStorageSmoke.Read<Declaration>(declarationPath);
        var auth = LabStorageSmoke.Read<InstallerLabUserDataAuthorizationV1>(authorizationPath);
        if (auth != declaration.Derivation.Authorization || auth.InitramfsSha256 != previous.ResultSha256 ||
            auth.CloseSha256 != previous.CloseSha256 || auth.AttemptId != declaration.RunId || auth.OperationId != declaration.OperationId)
            throw new InvalidDataException("UserDataOperationBindingRejected");
        return (declaration, auth);
    }

    // Calls the SAME protected readers; has no inventory collection, reservation,
    // authority/session construction, block access, mounting or publication path.
    internal static int Readiness(string predecessor, string pin, string? declaration = null, string? authorization = null)
    {
        var boundary = "PredecessorReopen";
        try
        {
            var (_, previous) = Reopen(predecessor);
            boundary = "ExternalDevelopmentPin";
            _ = ReadPin(pin, previous);
            if (declaration is not null && authorization is not null)
            {
                boundary = "OperationBinding";
                _ = ReadOperation(declaration, authorization, previous);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Passed", previous.ResultSha256,
                previous.CloseSha256, EffectAuthorization = false }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Rejected", Boundary = boundary,
                Code = FailureCode(error), ErrorType = error.GetType().Name, EffectAuthorization = false }));
            return 2;
        }
    }

    private static string FailureCode(Exception error) => error.Message switch
    {
        "UserDataOperationBindingRejected" => "UserDataOperationBindingRejected",
        "UserDataExternalPinAuthenticationRejected" => "UserDataExternalPinAuthenticationRejected",
        _ => "UserDataSessionUnavailable"
    };

    internal static async Task<int> RunAsync(string predecessorPath, string declarationPath, string authorizationPath,
        string runtimePath, string journalPath, string pinPath, string transferPath, string deliveryPath)
    {
        var invoked = false;
        var boundary = "PredecessorReopen";
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
            var (previousStorage, previous) = Reopen(predecessorPath);
            boundary = "ExternalDevelopmentPin";
            var pin = ReadPin(pinPath, previous);
            boundary = "OperationBinding";
            var (declaration, auth) = ReadOperation(declarationPath, authorizationPath, previous);
            boundary = "SourceBinding";
            DebianSessionToolProtection.Verify(transferPath); DebianSessionToolProtection.Verify(deliveryPath);
            var transfer = await File.ReadAllBytesAsync(transferPath).ConfigureAwait(false); var delivery = await File.ReadAllBytesAsync(deliveryPath).ConfigureAwait(false);
            if (Hash(transfer) != auth.TransferSha256 || Hash(delivery) != declaration.Derivation.DeliverySha256)
                throw new InvalidDataException("UserData delivery identity changed.");
            var delivered = JsonSerializer.Deserialize<JsonElement>(delivery);
            if (delivered.GetProperty("TargetCopySha256").GetString() != auth.TargetSha256 ||
                delivered.GetProperty("StagedTargetSha256").GetString() != declaration.Derivation.StagedTargetSha256)
                throw new InvalidDataException("UserData staging copy ancestry changed.");
            boundary = "StorageAndJournalValidation";
            var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(5));
            var inventory = await LabStorageSmoke.CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
            var derived = InstallationStorage.ContinueLabInitramfsCheckpoint(previousStorage, declaration.Derivation,
                Hash(await File.ReadAllBytesAsync(authorizationPath, deadline.Token).ConfigureAwait(false)), declaration.HostObservationSha256, declaration.CreatedBackings,
                declaration.ReopenedBackings, inventory, LabStorageSmoke.ReadGuest(inventory));
            if (derived.Availability != ObservationAvailability.Available) throw new InvalidDataException(derived.Code);
            var storage = derived.Value;
            var plan = new DebianLabUserDataPlanV1(1, auth.OperationId, storage.Provenance, previous.ResultSha256, previous.CloseSha256,
                auth.RetentionSha256, storage.Continuation!.DerivationSha256!, declaration.Derivation, transfer, delivery,
                Hash(JsonSerializer.SerializeToUtf8Bytes(runtime.ToolHashes)));
            _ = plan.Fingerprint(storage, previous);
            var stores = LabStorageSmoke.Read<JournalDeclaration>(journalPath);
            var parent = "/lab-journal/userdata/" + auth.AttemptId.ToString("D");
            if (stores.SessionStore != parent + "/session" || stores.ImportStore != parent + "/effects")
                throw new InvalidDataException("UserData journal location changed.");
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage, stores.SessionStore,
                stores.ImportStore, stores.RuntimeFileSystemUuid, deadline.Token);
            var sessionStore = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath, stores.ToolSha256, witnesses[0], runtime);
            var effectStore = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath, stores.ToolSha256, witnesses[1], runtime);
            var authority = DebianMountSessionAuthority.ForLabUserData(storage, previous, plan, sessionStore, effectStore, witnesses[1],
                pin);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "UserDataPreflight", Plan = plan,
                authority.PlanSha256, authority.SessionId, Stores = witnesses }));
            boundary = "CanonicalSession";
            await using var session = new DebianNativeMountSession(authority); invoked = true;
            await session.StartAsync(runtime, deadline.Token);
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.TransferUserData,
                DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                boundary = action.ToString();
                var result = await session.PerformAsync(action, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result, authority.LastResultReference, authority.LastResultSha256 }));
            }
            var close = await sessionStore.ReopenAsync(authority.LastResultReference!, deadline.Token);
            if (Hash(close) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed)
                throw new IOException("UserData Close reopen failed.");
            // Independent full-chain verification is also required by the retained
            // host readback before the lab handoff can be called qualified.
            Console.WriteLine(JsonSerializer.Serialize(new { UserData = "SessionAppliedAndVerified", Teardown = "SessionAppliedAndVerified",
                IndependentHandoff = "Required", plan.OperationId, authority.SessionId, authority.PlanSha256,
                authority.LastResultReference, authority.LastResultSha256, ProductionAuthentication = "Unsupported", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or
            ArgumentException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { UserData = invoked ? "OutcomeUnknown" : "NotStarted",
                Code = FailureCode(error), Boundary = boundary, ErrorType = error.GetType().Name }));
            return 2;
        }
    }
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    private readonly record struct Binding(string InitramfsSha256, string CloseSha256);
    private readonly record struct InitramfsDeclaration(Guid RunId, Guid OperationId, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
        InstallerLabInitramfsDerivationV1 Derivation);
    private readonly record struct Declaration(Guid RunId, Guid OperationId, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
        InstallerLabUserDataDerivationV1 Derivation);
}
