using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;

// Opt-in lab composition of the SAME authority/transport/importer. No provisioning,
// synthetic inventory, fallback source, fixture lease, package or firmware execution.
// Required lab setup and persistent-store qualification are documented beside this tool.
if (args.Length != 6 || args[0] is not ("--check" or "--run-development"))
    throw new ArgumentException("Usage: --check|--run-development plan.json descriptor.json external-pin.json runtime.json journal-declaration.json");

var plan = JsonSerializer.Deserialize<DebianConfiguredRootImportPlanV1>(await File.ReadAllBytesAsync(args[1])) ?? throw new InvalidDataException("Missing import plan.");
var descriptor = await File.ReadAllBytesAsync(args[2]); // diagnostic copy only; actual import reopens from the canonical payload
var pin = JsonSerializer.Deserialize<DebianRootDevelopmentPinV1>(await File.ReadAllBytesAsync(args[3])) ?? throw new InvalidDataException("Missing external pin.");
var artifact = DebianConfiguredRootArtifacts.ReopenDevelopmentPinned(descriptor, pin, DateTimeOffset.UtcNow);
if (plan.BuildId != artifact.BuildId || plan.DerivationId != artifact.Attestation.Neutralization?.DerivationId ||
    plan.DescriptorSha256 != DebianConfiguredRootArtifacts.Digest(descriptor) || plan.ContentSha256 != artifact.Content.Sha256 ||
    plan.ManifestSha256 != artifact.Manifest.Sha256 || plan.ContentLength != artifact.Content.Length || plan.PolicySha256 != artifact.PackageSet.PolicySha256)
    throw new InvalidDataException("Diagnostic artifact differs from session plan.");
var fingerprint = plan.Fingerprint();
Console.WriteLine(JsonSerializer.Serialize(new { Phase = "Preflight", PlanSha256 = fingerprint, plan.BuildId, plan.DerivationId,
    TargetGeneration = plan.Root.GenerationId, Transport = plan.TransportSupport.Availability.ToString(), plan.TransportSupport.Code,
    ProductionAuthentication = "Unsupported", Execution = "NotStarted" }));
if (plan.TransportSupport.Availability != ObservationAvailability.Available) return 2;
if (args[0] == "--check") return 0;
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated disposable Linux VM required.");

var runtime = JsonSerializer.Deserialize<DebianSessionRuntimeV1>(await File.ReadAllBytesAsync(args[4])) ?? throw new InvalidDataException("Missing runtime.");
using var journalDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(args[5]));
var declared = journalDocument.RootElement;
var stores = new JournalDeclaration(declared.GetProperty("SessionStore").GetString()!, declared.GetProperty("ImportStore").GetString()!,
    declared.GetProperty("ToolPath").GetString()!, declared.GetProperty("ToolSha256").GetString()!, declared.GetProperty("RuntimeFileSystemUuid").GetGuid());
// Independently inspect actual storage before the first journal reservation. These stores
// cannot live on the target/protected disk, read-only payload/ESP or a volatile runtime mount.
if (stores.SessionStore == stores.ImportStore || !Path.IsPathFullyQualified(stores.SessionStore) ||
    !Path.IsPathFullyQualified(stores.ImportStore)) throw new InvalidDataException("Separate absolute journal stores required.");
var witnesses = await DebianNativeMountSession.ObserveImportJournalsAsync(runtime, plan, stores.SessionStore,
    stores.ImportStore, stores.RuntimeFileSystemUuid, CancellationToken.None);
var sessionJournal = new DebianLinuxDeploymentJournal(stores.SessionStore, stores.ToolPath, stores.ToolSha256, witnesses[0], runtime);
var importJournal = new DebianLinuxDeploymentJournal(stores.ImportStore, stores.ToolPath, stores.ToolSha256, witnesses[1], runtime);
var authority = DebianMountSessionAuthority.ForDevelopmentImport(plan, sessionJournal, importJournal, pin);
await using var session = new DebianNativeMountSession(authority);
using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
await session.StartAsync(runtime, cancellation.Token);
foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
    DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload, DebianMountSessionAction.ImportConfiguredRoot,
    DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
{
    // Result diagnostics are NOT durable evidence; the two Linux journals own that evidence.
    // No finally-based lazy cleanup or automatic retry after an uncertain action.
    Console.WriteLine((await session.PerformAsync(action, cancellation.Token)).GetRawText());
}
return 0;

internal sealed record JournalDeclaration(string SessionStore, string ImportStore, string ToolPath, string ToolSha256, Guid RuntimeFileSystemUuid);
