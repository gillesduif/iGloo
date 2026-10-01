using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

internal static class LabConfiguredRootImport
{
    internal static async Task<int> RunAsync(string storagePath, string descriptorPath, string pinPath,
        string transportPath, string runtimePath, string journalsPath)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated Linux lab required.");
        var started = false;
        var invoked = false;
        try
        {
            var evidence = LabStorageSmoke.Read<InstallerLabStorageEvidenceV1>(storagePath);
            var runtime = LabStorageSmoke.Read<DebianSessionRuntimeV1>(runtimePath);
            var stores = LabStorageSmoke.Read<JournalDeclaration>(journalsPath);
            // The retained external envelope has additional explicit development-only fields.
            // Read and validate that exact envelope; never issue a pin from descriptor claims.
            var external = LabStorageSmoke.Read<ExternalPin>(pinPath);
            if (external.Use != "DevelopmentImportOnly" || external.ProductionAuthentication != "Unsupported")
                throw new InvalidDataException("External development authority required.");
            var pin = new DebianRootDevelopmentPinV1(external.BuildId, external.DescriptorSha256,
                external.PolicySha256, external.NotBeforeUtc, external.NotAfterUtc);
            DebianSessionToolProtection.Verify(descriptorPath);
            DebianSessionToolProtection.Verify(transportPath);
            var descriptor = await File.ReadAllBytesAsync(descriptorPath);
            var artifact = DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(descriptor, pin, DateTimeOffset.UtcNow);
            if (external.ManifestSha256 != artifact.Manifest.Sha256 || external.ContentSha256 != artifact.Content.Sha256)
                throw new InvalidDataException("External content binding changed.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(5));
            var inventory = await LabStorageSmoke.CollectAsync(runtime.Collector, runtime.ToolHashes[runtime.Collector], deadline.Token);
            var validated = InstallationStorage.VerifyLab(evidence, inventory, LabStorageSmoke.ReadGuest(inventory));
            if (validated.Availability != ObservationAvailability.Available) throw new InvalidDataException(validated.Code);
            var storage = validated.Value;
            var transportBytes = await File.ReadAllBytesAsync(transportPath);
            var plan = new DebianLabImportPlanV1(1, storage.Provenance, artifact.BuildId,
                artifact.Attestation.Neutralization!.DerivationId, pin.DescriptorSha256, pin.PolicySha256,
                artifact.Manifest.Sha256, artifact.Content.Sha256, artifact.Content.Length, "Chunked",
                DebianConfiguredRootArtifacts.Digest(transportBytes), 0);
            plan.VerifyArtifact(artifact, descriptor);
            _ = DebianRootTransports.Reopen(transportBytes, plan);
            var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, storage,
                stores.SessionStore, stores.ImportStore, stores.RuntimeFileSystemUuid, deadline.Token);
            var journal = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath, stores.ToolSha256, witnesses[0], runtime);
            var imports = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath, stores.ToolSha256, witnesses[1], runtime);
            var authority = DebianMountSessionAuthority.ForLabDevelopmentImport(storage, plan, journal, imports, pin);
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = "Preflight", Plan = plan, authority.PlanSha256,
                ExternalPinSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pinPath))), Stores = witnesses }));
            await using var session = new DebianNativeMountSession(authority);
            started = true;
            await session.StartAsync(runtime, deadline.Token);
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.ImportConfiguredRoot,
                DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            {
                if (action == DebianMountSessionAction.ImportConfiguredRoot) invoked = true;
                var result = await session.PerformAsync(action, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Result = result, authority.LastResultReference, authority.LastResultSha256 }));
            }
            var reopened = await journal.ReopenAsync(authority.LastResultReference!, deadline.Token);
            if (Convert.ToHexString(SHA256.HashData(reopened)) != authority.LastResultSha256 || authority.State != DebianMountSessionState.Closed)
                throw new IOException("Final session result reopen failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { storage.Provenance, authority.SessionId, authority.GenerationId, authority.PlanSha256,
                authority.LastResultReference, authority.LastResultSha256, Import = "AppliedAndVerified", Teardown = "AppliedAndVerified",
                ProductionAuthentication = "Unsupported", NativeSupported = 0 }));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or OperationCanceledException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Scope = "ConfiguredRootImport", Session = started ? "OutcomeUnknown" : "NotStarted",
                ImportDispatchInvoked = invoked, ErrorType = error.GetType().Name, Code = error.Message }));
            return 2;
        }
    }

    private readonly record struct ExternalPin(string Use, string ProductionAuthentication, Guid BuildId, string DescriptorSha256,
        string PolicySha256, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, string ManifestSha256, string ContentSha256);
}
