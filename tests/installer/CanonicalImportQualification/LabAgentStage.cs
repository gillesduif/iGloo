using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

// Read-only protected predecessor reopening. A valid historical chain alone
// never authorizes a new derivative or a mount session.
internal static class LabAgentStage
{
    // The caller names a protected completion store, not the QEMU-owned run
    // export. An ordinary root-owned diagnostic copy has no terminal anchor.
    internal static (ValidatedInstallationStorage Storage, DebianVerifiedUserData Verified) ReopenTrustedCompletion(
        string completionPath, string authorizationPath, bool requireLive = false)
    {
        var auth = LabStorageSmoke.Read<InstallerLabAgentStageAuthorizationV1>(authorizationPath);
        var anchorPath = Path.Combine(completionPath, "anchor.json");
        var resultPath = Path.Combine(completionPath, "result.json");
        var anchor = LabStorageSmoke.Read<CompletionAnchor>(anchorPath);
        var result = LabStorageSmoke.Read<CompletionPublication>(resultPath);
        var live = anchor is { Version: 2, Scope: "TrustedLiveUserDataTerminalV2" };
        if (auth is not { Version: 1, Scope: "OneUserDataCheckpointOfflineAgentStage" } ||
            !(live || anchor is { Version: 1, Scope: "TrustedUserDataTerminalV1" }) ||
            (requireLive && !live) || anchor.Files is null ||
            result is not { Version: 1, Scope: "ProtectedUserDataCompletionV1", Outcome: "AppliedAndVerified" } ||
            auth.GenerationId != anchor.GenerationId || auth.AttemptId == anchor.SourceRunId ||
            auth.OperationId == anchor.OperationId || auth.UserDataSha256 != anchor.ResultSha256 ||
            auth.CloseSha256 != anchor.CloseSha256 || auth.RetentionSha256 != anchor.RetentionSha256 ||
            auth.TargetSha256 != anchor.TargetSha256 || auth.JournalSha256 != anchor.JournalSha256 ||
            result.AnchorSha256 != Hash(File.ReadAllBytes(anchorPath)) ||
            result.AuthorizationSha256 != Hash(File.ReadAllBytes(authorizationPath)))
            throw new InvalidDataException("AgentStageCompletionAnchorRejected");
        if (live)
        {
            var capturePath = Path.Combine(completionPath, "capture.json");
            var packetPath = Path.Combine(completionPath, "packet.bin");
            var capture = LabStorageSmoke.Read<JsonElement>(capturePath);
            DebianSessionToolProtection.Verify(packetPath);
            if (anchor.CaptureSha256 is null || Hash(File.ReadAllBytes(capturePath)) != anchor.CaptureSha256 ||
                new FileInfo(packetPath).Length > 24 * 1024 * 1024 ||
                Hash(File.ReadAllBytes(packetPath)) != capture.GetProperty("PacketSha256").GetString() ||
                capture.GetProperty("Scope").GetString() != "PendingControlledUserDataCaptureV1" ||
                capture.GetProperty("Status").GetString() != "PendingBackingReadback" ||
                capture.GetProperty("RunId").GetGuid() != anchor.SourceRunId ||
                capture.GetProperty("GenerationId").GetGuid() != anchor.GenerationId ||
                capture.GetProperty("OperationId").GetGuid() != anchor.OperationId ||
                capture.GetProperty("SessionId").GetGuid() != anchor.SessionId ||
                capture.GetProperty("ResultSha256").GetString() != anchor.ResultSha256 ||
                capture.GetProperty("CloseSha256").GetString() != anchor.CloseSha256)
                throw new InvalidDataException("AgentStageCompletionCaptureRejected");
            var captureFiles = capture.GetProperty("Files");
            if (captureFiles.ValueKind != JsonValueKind.Object || captureFiles.EnumerateObject().Any(p =>
                !anchor.Files.TryGetValue(p.Name, out var expectedDigest) ||
                expectedDigest != p.Value.GetString()))
                throw new InvalidDataException("AgentStageCompletionCaptureInventoryRejected");
        }
        var raw = Path.Combine(completionPath, "raw");
        var expected = anchor.Files;
        if (expected is null || expected.Count is < 6 or > 4096 || result.Files is null ||
            !expected.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(
                result.Files.OrderBy(p => p.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("AgentStageCompletionInventoryRejected");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(raw);
        while (pending.Count != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("AgentStageCompletionLinkRejected");
                var name = Path.GetRelativePath(raw, entry).Replace('\\', '/');
                if (Directory.Exists(entry)) { actualDirectories.Add(name); pending.Push(entry); }
                else actual.Add(name);
                if (actual.Count + actualDirectories.Count > 8192)
                    throw new InvalidDataException("AgentStageCompletionInventoryUnbounded");
            }
        }
        if (!actual.SetEquals(expected.Keys)) throw new InvalidDataException("AgentStageCompletionInventoryRejected");
        var expectedDirectories = expected.Keys.SelectMany(name =>
        {
            var parts = name.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(i => string.Join('/', parts.Take(i)));
        }).ToHashSet(StringComparer.Ordinal);
        if (!actualDirectories.SetEquals(expectedDirectories))
            throw new InvalidDataException("AgentStageCompletionDirectoryChanged");
        long total = 0;
        foreach (var (name, digest) in expected)
        {
            if (name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or "..") ||
                digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException("AgentStageCompletionPathRejected");
            var path = Path.Combine(raw, name.Replace('/', Path.DirectorySeparatorChar));
            DebianSessionToolProtection.Verify(path);
            var bytes = File.ReadAllBytes(path);
            total = checked(total + bytes.Length);
            if (total > 128 * 1024 * 1024 || Hash(bytes) != digest)
                throw new InvalidDataException("AgentStageCompletionBytesChanged");
        }
        var (storage, previous) = ReopenUserData(raw);
        if (storage.Provenance.RunId != anchor.SourceRunId || storage.Provenance.GenerationId != anchor.GenerationId ||
            previous.Plan.OperationId != anchor.OperationId || previous.ResultSha256 != anchor.ResultSha256 ||
            previous.CloseSha256 != anchor.CloseSha256 ||
            Hash(File.ReadAllBytes(Path.Combine(raw, "userdata-retention.json"))) != anchor.RetentionSha256)
            throw new InvalidDataException("AgentStageCompletionTerminalChanged");
        return (storage, previous);
    }

    internal static (ValidatedInstallationStorage Storage, DebianVerifiedUserData Verified) ReopenUserData(string directory)
    {
        var (previousStorage, previous) = LabUserData.Reopen(Path.Combine(directory, "input", "initramfs-predecessor"));
        var authorizationPath = Path.Combine(directory, "input", "userdata-authorization.json");
        var auth = LabStorageSmoke.Read<InstallerLabUserDataAuthorizationV1>(authorizationPath);
        var derivation = LabStorageSmoke.Read<InstallerLabUserDataDerivationV1>(Path.Combine(directory, "userdata-derivation.json"));
        var host = LabStorageSmoke.Read<JsonElement>(Path.Combine(directory, "guest-evidence", "execution", "host-binding.json"));
        if (auth != derivation.Authorization || auth.InitramfsSha256 != previous.ResultSha256 ||
            auth.CloseSha256 != previous.CloseSha256 || auth.AttemptId != host.GetProperty("RunId").GetGuid())
            throw new InvalidDataException("AgentUserDataPredecessorBindingChanged");
        var inventoryPath = Path.Combine(directory, "guest-evidence", "execution", "inventory.json");
        DebianSessionToolProtection.Verify(inventoryPath);
        var inventory = LinuxInstallerInventoryProtocol.Parse(File.ReadAllText(inventoryPath));
        var historical = LabStorageSmoke.Read<InstallerLabStorageEvidenceV1>(Path.Combine(directory, "input",
            "initramfs-predecessor", "configured", "import", "storage.json"));
        var derived = InstallationStorage.ContinueLabInitramfsCheckpoint(previousStorage, derivation,
            Hash(File.ReadAllBytes(authorizationPath)), host.GetProperty("HostObservationSha256").GetString()!,
            host.GetProperty("CreatedBackings").Deserialize<ImmutableArray<InstallerLabBackingV1>>(),
            host.GetProperty("ReopenedBackings").Deserialize<ImmutableArray<InstallerLabBackingV1>>(),
            inventory, Observations.Available(historical.GuestDisks));
        if (derived.Availability != ObservationAvailability.Available) throw new InvalidDataException(derived.Code);
        var stdoutPath = Path.Combine(directory, "guest-evidence", "execution", "userdata.stdout");
        DebianSessionToolProtection.Verify(stdoutPath);
        var first = File.ReadLines(stdoutPath).First();
        LabStorageSmoke.RequireUnambiguousJson(System.Text.Encoding.UTF8.GetBytes(first));
        using var preflight = JsonDocument.Parse(first);
        var root = preflight.RootElement;
        if (root.GetProperty("Phase").GetString() != "UserDataPreflight")
            throw new InvalidDataException("AgentUserDataPreflightMissing");
        var plan = root.GetProperty("Plan").Deserialize<DebianLabUserDataPlanV1>()!;
        if (plan.Fingerprint(derived.Value, previous) != root.GetProperty("PlanSha256").GetString())
            throw new InvalidDataException("AgentUserDataPlanChanged");
        var generation = derived.Value.Provenance.GenerationId.ToString("D");
        var journals = Path.Combine(directory, "guest-evidence", "journals");
        var readback = LabStorageSmoke.Read<JsonElement>(Path.Combine(directory, "userdata-independent-host-readback.json"));
        var verified = readback.GetProperty("Verified");
        if (verified.GetProperty("UserData").GetString() != "AppliedAndVerified" ||
            verified.GetProperty("Teardown").GetString() != "AppliedAndVerified" ||
            verified.GetProperty("PlanSha256").GetString() != root.GetProperty("PlanSha256").GetString())
            throw new InvalidDataException("AgentUserDataIndependentHandoffChanged");
        var effects = verified.GetProperty("EffectRecords").EnumerateArray().ToArray();
        var sessions = verified.GetProperty("SessionRecords").EnumerateArray().ToArray();
        var predecessor = DebianVerifiedUserData.Reopen(derived.Value, previous, plan,
            LabConfiguration.ReadChain(Path.Combine(journals, "session", generation)),
            LabConfiguration.ReadChain(Path.Combine(journals, "effects", generation)),
            effects[^1].GetProperty("Sha256").GetString()!, sessions[^1].GetProperty("Sha256").GetString()!);
        var producer = LabConfiguration.ReadChain(Path.Combine(journals, "effects", plan.OperationId.ToString("D")));
        using var bundle = JsonDocument.Parse(predecessor.AdmissionBundle);
        var embedded = bundle.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        var receiptPath = Path.Combine(journals, "effects", "userdata-receipt.json");
        DebianSessionToolProtection.Verify(receiptPath);
        if (producer.Length != 3 || embedded.Length != producer.Length ||
            embedded.Where((item, i) => !item.GetBytesFromBase64().AsSpan().SequenceEqual(producer[i])).Any() ||
            !File.ReadAllBytes(receiptPath).AsSpan().SequenceEqual(predecessor.AdmissionReceipt.Span))
            throw new InvalidDataException("AgentUserDataProducerJournalChanged");
        if (verified.GetProperty("FilesystemSha256").GetString() != predecessor.Observation.GetProperty("FilesystemSha256").GetString() ||
            verified.GetProperty("PackageStateSha256").GetString() != predecessor.Observation.GetProperty("PackageStateSha256").GetString())
            throw new InvalidDataException("AgentUserDataIndependentStateChanged");
        return (derived.Value, predecessor);
    }

    internal static int Readiness(string directory)
    {
        var boundary = "InitramfsPredecessor";
        try
        {
            _ = LabUserData.Reopen(Path.Combine(directory, "input", "initramfs-predecessor"));
            boundary = "UserDataProtectedChains";
            var (_, predecessor) = ReopenUserData(directory);
            var serializedPredecessor = JsonSerializer.SerializeToUtf8Bytes(predecessor);
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Passed", predecessor.ResultSha256,
                predecessor.CloseSha256,
                SerializedPredecessorBytes = serializedPredecessor.Length,
                FilesystemSha256 = predecessor.Observation.GetProperty("FilesystemSha256").GetString(),
                PackageStateSha256 = predecessor.Observation.GetProperty("PackageStateSha256").GetString(),
                AgentStageAuthority = false, EffectAuthorization = false }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            var detail = error.Message.StartsWith("UserData observation representation changed: ", StringComparison.Ordinal) &&
                new[] { "Baseline", "Transfer", "Admission", "Verify" }.Any(error.Message.EndsWith)
                ? error.Message : error.Message is "Completed UserData predecessor chains required." or
                "UserData session missing." or "UserData predecessor lineage changed." or
                "UserData predecessor lease set changed." or "UserData predecessor effect order changed." or
                "UserData independent observation changed." or "UserData observation representation changed." or
                "UserData producer admission changed." or
                "UserData admitted result changed." or "UserData exact Close/teardown missing."
                ? error.Message : "OtherProtectedReaderRejection";
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Rejected", Code = "AgentUserDataPredecessorUnavailable",
                Boundary = boundary, ErrorType = error.GetType().Name,
                FailureMethod = error.TargetSite?.Name, Detail = detail,
                FailureLine = new System.Diagnostics.StackTrace(error, true).GetFrames()?
                    .Where(f => f.GetFileName()?.EndsWith("DebianUserDataPredecessor.cs", StringComparison.Ordinal) == true ||
                                f.GetFileName()?.EndsWith("LabAgentStage.cs", StringComparison.Ordinal) == true)
                    .Select(f => f.GetFileLineNumber()).FirstOrDefault(),
                EffectAuthorization = false }));
            return 2;
        }
    }

    internal static int CompletionReadiness(string directory, string authorizationPath)
    {
        try
        {
            var (_, previous) = ReopenTrustedCompletion(directory, authorizationPath);
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Passed", previous.ResultSha256,
                previous.CloseSha256, EffectAuthorization = false }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Readiness = "Rejected",
                Code = "AgentStageTrustedCompletionUnavailable", ErrorType = error.GetType().Name,
                EffectAuthorization = false }));
            return 2;
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    // This entrypoint cannot accept a fixture packet as predecessor authority.
    // ReopenUserData applies the protected original-path reader before any
    // inventory, block acquisition, reservation or session effect.
    internal static async Task<int> RunAsync(string predecessorPath, string declarationPath,
        string authorizationPath, string runtimePath, string journalPath, string pinPath)
    {
        var invoked = false; var boundary = "ProtectedPredecessor";
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
            var (previousStorage, previous) = ReopenTrustedCompletion(predecessorPath, authorizationPath, requireLive: true);
            boundary = "ExternalDevelopmentPin";
            var pin = LabUserData.ReadPin(pinPath, previous.Initramfs);
            boundary = "AgentStageOperationBinding";
            var declaration = LabStorageSmoke.Read<Declaration>(declarationPath);
            var auth = LabStorageSmoke.Read<InstallerLabAgentStageAuthorizationV1>(authorizationPath);
            var retentionPath = Path.Combine(predecessorPath, "raw", "userdata-retention.json");
            DebianSessionToolProtection.Verify(retentionPath);
            if (auth != declaration.Derivation.Authorization || auth.UserDataSha256 != previous.ResultSha256 ||
                auth.CloseSha256 != previous.CloseSha256 || auth.AttemptId != declaration.RunId ||
                auth.OperationId != declaration.OperationId ||
                auth.RetentionSha256 != Hash(await File.ReadAllBytesAsync(retentionPath).ConfigureAwait(false)))
                throw new InvalidDataException("AgentStageOperationBindingRejected");
            boundary = "StorageAndJournalValidation";
            var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(5));
            await DebianNativeMountSession.VerifyRuntimeAsync(runtime, deadline.Token, requireAgentStage: true)
                .ConfigureAwait(false);
            var inventory = await LabStorageSmoke.CollectAsync(runtime.Collector,
                runtime.ToolHashes[runtime.Collector], deadline.Token).ConfigureAwait(false);
            var derived = InstallationStorage.ContinueLabUserDataCheckpoint(previousStorage, declaration.Derivation,
                Hash(await File.ReadAllBytesAsync(authorizationPath, deadline.Token).ConfigureAwait(false)),
                declaration.HostObservationSha256, declaration.CreatedBackings, declaration.ReopenedBackings,
                inventory, LabStorageSmoke.ReadGuest(inventory));
            if (derived.Availability != ObservationAvailability.Available) throw new InvalidDataException(derived.Code);
            var storage = derived.Value;
            var root = storage.Receipts.Single(r => r.FileSystem.Type == "EXT4").Creation.Partition.PartitionGuid;
            var plan = new DebianLabAgentStagePlanV1(1, auth.OperationId, storage.Provenance, previous.ResultSha256,
                previous.CloseSha256, auth.RetentionSha256, storage.Continuation!.DerivationSha256!,
                declaration.Derivation, DebianAgentProfiles.DeclareLabOfflineStage(storage.Provenance.GenerationId,
                    auth.OperationId, root), Hash(JsonSerializer.SerializeToUtf8Bytes(runtime.ToolHashes)));
            _ = plan.Fingerprint(storage, previous);
            var stores = LabStorageSmoke.Read<JournalDeclaration>(journalPath);
            var parent = "/lab-journal/agent-stage/" + auth.AttemptId.ToString("D");
            if (stores.SessionStore != parent + "/session" || stores.ImportStore != parent + "/effects")
                throw new InvalidDataException("AgentStageJournalLocationChanged");
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage,
                stores.SessionStore, stores.ImportStore, stores.RuntimeFileSystemUuid, deadline.Token).ConfigureAwait(false);
            var sessionStore = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath,
                stores.ToolSha256, witnesses[0], runtime);
            var effectStore = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath,
                stores.ToolSha256, witnesses[1], runtime);
            var authority = DebianMountSessionAuthority.ForLabOfflineAgentStage(storage, previous, plan,
                sessionStore, effectStore, pin);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "AgentStagePreflight", Plan = plan,
                authority.PlanSha256, authority.SessionId, Stores = witnesses }));
            boundary = "CanonicalSession";
            await using var session = new DebianNativeMountSession(authority); invoked = true;
            await session.StartAsync(runtime, deadline.Token).ConfigureAwait(false);
            string? effectHash = null;
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases,
                DebianMountSessionAction.PrepareImportMountpoints, DebianMountSessionAction.MountRoot,
                DebianMountSessionAction.MountPayload, DebianMountSessionAction.StageOfflineAgent,
                DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload,
                DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                boundary = action.ToString();
                var result = await session.PerformAsync(action, deadline.Token).ConfigureAwait(false);
                if (action == DebianMountSessionAction.StageOfflineAgent)
                    effectHash = result.GetProperty("AgentStageResult").GetProperty("Sha256").GetString();
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result,
                    authority.LastResultReference, authority.LastResultSha256 }));
            }
            var close = await sessionStore.ReopenAsync(authority.LastResultReference!, deadline.Token).ConfigureAwait(false);
            if (Hash(close) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed ||
                effectHash is null) throw new IOException("Agent stage Close reopen failed.");
            // The protected journal reader independently reopens complete chains,
            // including the exact Unmount/Close mount deltas. A session exit alone
            // cannot promote this result to StagedInputsPending.
            var generation = storage.Provenance.GenerationId.ToString("D");
            var verified = DebianVerifiedAgentStage.Reopen(storage, previous, plan,
                LabConfiguration.ReadChain(Path.Combine(stores.SessionStore, generation)),
                LabConfiguration.ReadChain(Path.Combine(stores.ImportStore, generation)),
                effectHash, authority.LastResultSha256);
            Console.WriteLine(JsonSerializer.Serialize(new { AgentStage = "AppliedAndVerified",
                Lifecycle = DebianVerifiedAgentStage.Lifecycle, Teardown = "AppliedAndVerified",
                verified.ResultSha256, verified.CloseSha256, verified.PlanSha256,
                ConfigurationPresent = false, EnableLinkPresent = false,
                ProductionAuthentication = "Unsupported", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { AgentStage = invoked ? "OutcomeUnknown" : "NotStarted",
                Code = "AgentStageBoundaryUnavailable", Boundary = boundary, ErrorType = error.GetType().Name,
                CanonicalResultPublished = false }));
            return 2;
        }
    }

    private readonly record struct Declaration(Guid RunId, Guid OperationId, string HostObservationSha256,
        ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
        InstallerLabAgentStageDerivationV1 Derivation);
    private readonly record struct CompletionAnchor(int Version, string Scope, Guid SourceRunId, Guid GenerationId,
        Guid OperationId, Guid SessionId, string ResultSha256, string CloseSha256, string RetentionSha256,
        string TargetSha256, string JournalSha256, Dictionary<string, string> Files, string? CaptureSha256);
    private readonly record struct CompletionPublication(int Version, string Scope, string AnchorSha256,
        string AuthorizationSha256, Dictionary<string, string> Files, string Outcome);
}
