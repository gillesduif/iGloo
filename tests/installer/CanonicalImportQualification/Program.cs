using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;

if (args.Length is 3 or 5 && args[0] == "--userdata-readiness")
    return CanonicalImportQualification.LabUserData.Readiness(args[1], args[2],
        args.Length == 5 ? args[3] : null, args.Length == 5 ? args[4] : null);

if (args.Length == 2 && args[0] == "--reopen-configured-predecessor")
{
    var (_, verified) = CanonicalImportQualification.LabInitramfs.Reopen(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new { Scope = "ReadOnlyConfiguredPredecessor", verified.ResultSha256,
        verified.CloseSha256, verified.PlanSha256, EffectAuthorization = false }));
    return 0;
}

if (args.Length == 2 && args[0] == "--reopen-initramfs-predecessor")
{
    try
    {
        var (_, verified) = CanonicalImportQualification.LabUserData.Reopen(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(new { Scope = "ReadOnlyInitramfsPredecessor", verified.ResultSha256,
            verified.CloseSha256, verified.PlanSha256, EffectAuthorization = false }));
        return 0;
    }
    catch (InvalidDataException error)
    {
        // Fixed validator diagnostics only; never echo paths, input JSON or stack locals.
        var code = error.Message switch
        {
            "Initramfs successor binding rejected." => "InitramfsPlanBinding",
            "Completed initramfs chains required." => "InitramfsChains",
            "Initramfs predecessor lineage changed." => "InitramfsLineage",
            "Initramfs target differs from configured predecessor." => "InitramfsTargetBinding",
            "Initramfs target or effect order changed." => "InitramfsEffectOrder",
            "Initramfs independent observation changed." => "InitramfsObservation",
            "Initramfs generator did not complete." => "InitramfsGeneration",
            "Initramfs publication differs." => "InitramfsPublication",
            "Initramfs Close or handoff missing." => "InitramfsClose",
            _ => "PredecessorValidationUnavailable"
        };
        Console.WriteLine(JsonSerializer.Serialize(new { Scope = "ReadOnlyInitramfsPredecessor", Code = code,
            Validator = new System.Diagnostics.StackTrace(error).GetFrame(0)?.GetMethod()?.DeclaringType?.Name, EffectAuthorization = false }));
        return 2;
    }
}

if (args.Length == 9 && args[0] == "--userdata-derived-lab")
    return await CanonicalImportQualification.LabUserData.RunAsync(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]);

if (args.Length == 8 && args[0] == "--initramfs-derived-lab")
    return await CanonicalImportQualification.LabInitramfs.RunAsync(args[1], args[2], args[3], args[4], args[5], args[6], args[7]);

if (args.Length == 7 && args[0] == "--configure-derived-lab")
    return await CanonicalImportQualification.LabConfiguration.RunAsync(args[1], args[2], args[3], args[4], args[5], args[6]);
if (args.Length == 6 && args[0] == "--configure-lab")
    return await CanonicalImportQualification.LabConfiguration.RunAsync(args[1], args[2], args[3], args[4], args[5]);

if (args.Length == 4 && args[0] == "--observe-inventory")
    return await CanonicalImportQualification.InventoryProbe.RunAsync(args[1], args[2], args[3]);
if (args.Length == 5 && args[0] == "--observe-lab")
    return await CanonicalImportQualification.InventoryProbe.RunAsync(args[1], args[2], args[4], args[3]);

if (args.Length == 3 && args[0] == "--verify-lab-transition")
    return await CanonicalImportQualification.LabTransitionProbe.RunAsync(args[1], args[2]);

if (args.Length == 4 && args[0] == "--smoke-development")
    return await CanonicalImportQualification.LabStorageSmoke.RunAsync(args[1], args[2], args[3]);

if (args.Length == 7 && args[0] == "--import-lab-development")
    return await CanonicalImportQualification.LabConfiguredRootImport.RunAsync(args[1], args[2], args[3], args[4], args[5], args[6]);

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
var actions = new List<DebianMountSessionAction> { DebianMountSessionAction.AcquireLeases,
    DebianMountSessionAction.PrepareImportMountpoints, DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload };
if (args[0] == "--run-development") actions.Add(DebianMountSessionAction.ImportConfiguredRoot);
actions.AddRange([DebianMountSessionAction.Inspect, DebianMountSessionAction.UnmountPayload,
    DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close]);
foreach (var action in actions)
{
    // Result diagnostics are NOT durable evidence; the two Linux journals own that evidence.
    // No finally-based lazy cleanup or automatic retry after an uncertain action.
    Console.WriteLine((await session.PerformAsync(action, cancellation.Token)).GetRawText());
}
return 0;

internal sealed record JournalDeclaration(string SessionStore, string ImportStore, string ToolPath, string ToolSha256, Guid RuntimeFileSystemUuid);
