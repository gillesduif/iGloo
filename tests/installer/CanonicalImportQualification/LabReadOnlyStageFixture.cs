using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;

namespace CanonicalImportQualification;

// Ordinary-file test input only. The importer and stage validators reopen these
// exact serialized records, but no disk, launch, authorization or custody is
// represented by this exporter. The command is not an effectful lab entrypoint.
internal static partial class LabReadOnlyStageFixture
{
    private const string FixtureParent = "/var/lib/igloo/stage-reader-fixtures";
    private static readonly string TargetHash = new('A', 64);
    private static readonly string JournalHash = new('B', 64);
    private static readonly string CheckpointHash = new('C', 64);
    private static readonly string RetentionHash = new('D', 64);
    private static readonly string HostHash = new('E', 64);
    private static readonly string ExecutionHash = new('F', 64);

    internal static int Export(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux fixture reader required.");
        var root = Path.GetFullPath(path);
        if (root != path || Path.GetDirectoryName(root) != FixtureParent ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _) ||
            Directory.Exists(root) || File.Exists(root))
            throw new InvalidDataException("Create-new scoped fixture root required.");
        for (var parent = FixtureParent; parent is not null;
             parent = Path.GetDirectoryName(parent))
            RequireProtectedParent(parent);
        Directory.CreateDirectory(root);

        var evidence = LabStorageFixture.Create(import: true);
        var original = LabStorageFixture.Validate(evidence);
        var inventory = LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted);
        if (inventory.Availability != ObservationAvailability.Available)
            throw new InvalidDataException("Fixture inventory invalid.");
        var importSession = Guid.NewGuid();
        var importLeases = Leases(original, importSession, evidence, inventory.Value);
        var import = Import(original, importLeases, importSession);
        var imported = DebianVerifiedImport.Reopen(original, import.Session, import.Effects,
            import.ResultHash, import.CloseHash);

        var configurationRun = Guid.NewGuid();
        var configurationOperation = Guid.NewGuid();
        var configurationSession = Guid.NewGuid();
        var failedOperation = Guid.NewGuid();
        var configAuth = new InstallerLabDerivedAuthorizationV1(1,
            "OneCheckpointDerivedCoreConfiguration", configurationRun,
            configurationOperation, evidence.GenerationId, failedOperation,
            CheckpointHash, imported.ResultSha256, imported.CloseSha256,
            TargetHash, JournalHash);
        var configAuthRaw = Raw(configAuth);
        var configCopies = Copies(evidence.CreatedBackings, 1000);
        var configBackings = Backings(evidence.CreatedBackings, configCopies);
        var configDerivation = new InstallerLabDerivationV1(configAuth, Hash(configAuthRaw),
            CheckpointHash, configCopies);
        var configuredStorage = InstallationStorage.ContinueLabCheckpoint(original,
            configAuth, configDerivation, Hash(configAuthRaw), HostHash,
            configBackings, configBackings, inventory, Observations.Available(evidence.GuestDisks));
        if (configuredStorage.Availability != ObservationAvailability.Available)
            throw new InvalidDataException(configuredStorage.Code);
        var configPlan = new DebianCoreConfigurationPlanV1(1, configurationOperation,
            configuredStorage.Value.Provenance, imported.ResultSha256,
            imported.CloseSha256, CheckpointHash, "igloo-lab-config", "iglootest",
            1000, "en_US.UTF-8", "Etc/UTC", "us")
            { DerivationSha256 = configuredStorage.Value.Continuation!.DerivationSha256 };
        var configAuthority = DebianMountSessionAuthority.ForLabConfiguration(configuredStorage.Value,
            imported, configPlan, new NoEffectJournal(), new NoEffectJournal(),
            new DebianRootDevelopmentPinV1(imported.BuildId, imported.DescriptorSha256,
                JournalHash, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10)));
        var configLeases = Leases(configuredStorage.Value, configurationSession, evidence, inventory.Value);
        var config = Configuration(configuredStorage.Value, imported, configPlan,
            configAuthority.PlanSha256, configLeases, configurationSession);
        var configured = DebianVerifiedConfiguration.Reopen(configuredStorage.Value,
            imported, configPlan, config.Session, config.Effects,
            config.ResultHash, config.CloseHash);

        var initramfsRun = Guid.NewGuid();
        var initramfsOperation = Guid.NewGuid();
        var initramfsSession = Guid.NewGuid();
        var initAuth = new InstallerLabInitramfsAuthorizationV1(1,
            "OneConfiguredCheckpointInitramfs", initramfsRun, initramfsOperation,
            evidence.GenerationId, configured.ResultSha256, configured.CloseSha256,
            RetentionHash, TargetHash, JournalHash);
        var initAuthRaw = Raw(initAuth);
        var initCopies = Copies(configBackings, 2000);
        var initBackings = Backings(configBackings, initCopies);
        var initDerivation = new InstallerLabInitramfsDerivationV1(initAuth,
            Hash(initAuthRaw), initCopies);
        var initStorage = InstallationStorage.ContinueLabConfiguredCheckpoint(
            configuredStorage.Value, initDerivation, Hash(initAuthRaw), HostHash,
            initBackings, initBackings, inventory, Observations.Available(evidence.GuestDisks));
        if (initStorage.Availability != ObservationAvailability.Available)
            throw new InvalidDataException(initStorage.Code);
        var candidate = JsonSerializer.SerializeToElement(new {
            Policy = DebianLabInitramfsPlanV1.Policy,
            KernelRelease = DebianLabInitramfsPlanV1.KernelRelease,
            ConfigurationResultSha256 = configured.ResultSha256,
            RootUuid = initStorage.Value.Receipts[2].FileSystem.Uuid,
            Output = DebianLabInitramfsPlanV1.Destination,
            Modules = "most", Resume = "none", Compression = "gzip",
            CpuVendor = "AuthenticAMD", StorageTopology = "VirtioBlockExt4",
            InputClosureSha256 = TargetHash, HookSetSha256 = JournalHash
        });
        var initPlan = new DebianLabInitramfsPlanV1(1, initramfsOperation,
            initStorage.Value.Provenance, configured.ResultSha256,
            configured.CloseSha256, RetentionHash,
            initStorage.Value.Continuation!.DerivationSha256!,
            configured.Observation.GetProperty("FilesystemDeltaSha256").GetString()!,
            candidate, ExecutionHash);
        var initPlanHash = initPlan.Fingerprint(initStorage.Value, configured);
        var initLeases = Leases(initStorage.Value, initramfsSession, evidence, inventory.Value);
        var init = Initramfs(initStorage.Value, configured, initPlan,
            initPlanHash, initLeases, initramfsSession);
        var verifiedInitramfs = DebianVerifiedInitramfs.Reopen(initStorage.Value,
            configured, initPlan, init.Session, init.Effects,
            init.ResultHash, init.CloseHash);

        var configurationRoot = Path.Combine(root, "configuration");
        var importRoot = Path.Combine(configurationRoot, "import");
        Write(Path.Combine(importRoot, "storage.json"), Raw(evidence));
        WriteChain(Path.Combine(importRoot, "session", evidence.GenerationId.ToString("D")),
            import.PlanHash, import.Session);
        WriteChain(Path.Combine(importRoot, "import", evidence.GenerationId.ToString("D")),
            import.PlanHash, import.Effects);
        Write(Path.Combine(configurationRoot, "continuation.json"), Raw(new LabConfiguration.Continuation(
            configurationRun, configurationOperation, CheckpointHash,
            imported.ResultSha256, imported.CloseSha256, HostHash,
            configBackings, configBackings, configDerivation)));
        Write(Path.Combine(configurationRoot, "derived-authorization.json"), configAuthRaw);
        Write(Path.Combine(configurationRoot, "continuation-inventory.json"),
            Encoding.UTF8.GetBytes(evidence.Transitions[^1].Formatted));
        Write(Path.Combine(configurationRoot, "configuration-plan.json"), Raw(configPlan));
        Write(Path.Combine(configurationRoot, "binding.json"), Raw(new {
            ConfigurationSha256 = config.ResultHash, CloseSha256 = config.CloseHash }));
        WriteChain(Path.Combine(configurationRoot, "session", evidence.GenerationId.ToString("D")),
            config.PlanHash, config.Session);
        WriteChain(Path.Combine(configurationRoot, "effects", evidence.GenerationId.ToString("D")),
            config.PlanHash, config.Effects);

        var initramfsRoot = Path.Combine(root, "initramfs");
        CopyTree(configurationRoot, Path.Combine(initramfsRoot, "configured"));
        Write(Path.Combine(initramfsRoot, "initramfs-authorization.json"), initAuthRaw);
        Write(Path.Combine(initramfsRoot, "declaration.json"), Raw(new {
            RunId = initramfsRun, OperationId = initramfsOperation,
            HostObservationSha256 = HostHash,
            CreatedBackings = initBackings, ReopenedBackings = initBackings,
            Derivation = initDerivation }));
        Write(Path.Combine(initramfsRoot, "inventory.json"),
            Encoding.UTF8.GetBytes(evidence.Transitions[^1].Formatted));
        Write(Path.Combine(initramfsRoot, "initramfs-plan.json"), Raw(initPlan));
        Write(Path.Combine(initramfsRoot, "binding.json"), Raw(new {
            InitramfsSha256 = init.ResultHash, CloseSha256 = init.CloseHash }));
        WriteChain(Path.Combine(initramfsRoot, "session", evidence.GenerationId.ToString("D")),
            init.PlanHash, init.Session);
        WriteChain(Path.Combine(initramfsRoot, "effects", evidence.GenerationId.ToString("D")),
            init.PlanHash, init.Effects);

        var (_, reopenedConfig) = LabInitramfs.Reopen(configurationRoot);
        var (_, reopenedInitramfs) = LabUserData.Reopen(initramfsRoot);
        if (reopenedConfig.ResultSha256 != config.ResultHash ||
            reopenedConfig.CloseSha256 != config.CloseHash ||
            reopenedInitramfs.ResultSha256 != init.ResultHash ||
            reopenedInitramfs.CloseSha256 != init.CloseHash)
            throw new InvalidDataException("Fixture reader reopen changed.");
        var configurationPreflight = new {
            Phase = "ConfigurationPreflight", Plan = configPlan,
            PlanSha256 = config.PlanHash, SessionId = configurationSession,
            Predecessor = imported
        };
        var initramfsPreflight = new {
            Phase = "InitramfsPreflight", Plan = initPlan,
            PlanSha256 = init.PlanHash, SessionId = initramfsSession
        };
        Write(Path.Combine(root, "configuration-preflight.json"), Raw(configurationPreflight));
        Write(Path.Combine(root, "initramfs-preflight.json"), Raw(initramfsPreflight));
        Write(Path.Combine(root, "fixture-metadata.json"), Raw(new {
            Version = 1, Scope = "SyntheticReadOnlyStageReaderFixtureV1",
            FixtureOnly = true, EffectAuthorization = false,
            Import = new { RunId = evidence.RunId, evidence.GenerationId,
                SessionId = importSession, PlanSha256 = import.PlanHash,
                ResultSha256 = import.ResultHash, CloseSha256 = import.CloseHash },
            Configuration = new { RunId = configurationRun, evidence.GenerationId,
                OperationId = configurationOperation, SessionId = configurationSession,
                PlanSha256 = config.PlanHash, ResultSha256 = config.ResultHash,
                CloseSha256 = config.CloseHash, TargetSha256 = TargetHash,
                JournalSha256 = JournalHash, Preflight = configurationPreflight },
            Initramfs = new { RunId = initramfsRun, evidence.GenerationId,
                OperationId = initramfsOperation, SessionId = initramfsSession,
                PlanSha256 = init.PlanHash, ResultSha256 = init.ResultHash,
                CloseSha256 = init.CloseHash, TargetSha256 = TargetHash,
                JournalSha256 = JournalHash, Preflight = initramfsPreflight }
        }));
        Console.WriteLine(JsonSerializer.Serialize(new {
            Scope = "SyntheticReadOnlyStageReaderFixtureV1", FixtureOnly = true,
            ConfigurationResultSha256 = config.ResultHash,
            InitramfsResultSha256 = init.ResultHash,
            EffectAuthorization = false
        }));
        return 0;
    }

    private static InstallerBlockLeaseSet Leases(ValidatedInstallationStorage storage,
        Guid session, InstallerLabStorageEvidenceV1 evidence, InstallerRuntimeInventoryV1 inventory)
    {
        var result = InstallerBlockLeases.Acquire(storage, session, DateTimeOffset.UtcNow,
            Observations.Available(inventory),
            Observations.Available(LabStorageFixture.Numbers(inventory)),
            Observations.Available(evidence.GuestDisks));
        if (result.Availability != ObservationAvailability.Available)
            throw new InvalidDataException(result.Code);
        return result.Value;
    }

    private static ImmutableArray<InstallerLabCopyV1> Copies(
        ImmutableArray<InstallerLabBackingV1> source, ulong destinationOffset) =>
        source.Select((backing, index) => new InstallerLabCopyV1(backing.Serial,
            backing.HostDevice, backing.HostInode, backing.HostDevice,
            destinationOffset + (ulong)index + 2, backing.Length,
            index == 0 ? TargetHash : JournalHash,
            index == 0 ? TargetHash : JournalHash)).ToImmutableArray();

    private static ImmutableArray<InstallerLabBackingV1> Backings(
        ImmutableArray<InstallerLabBackingV1> source,
        ImmutableArray<InstallerLabCopyV1> copies) =>
        source.Select((backing, index) => backing with {
            HostInode = copies[index].DestinationInode
        }).ToImmutableArray();

    private static StageRecords Import(ValidatedInstallationStorage storage,
        InstallerBlockLeaseSet leases, Guid session)
    {
        var effects = ImmutableArray.CreateBuilder<byte[]>();
        var sessions = ImmutableArray.CreateBuilder<byte[]>();
        var planHash = TargetHash;
        var build = Guid.NewGuid();
        var derivation = Guid.NewGuid();
        var observation = JsonSerializer.SerializeToElement(new SortedDictionary<string, object?> {
            ["Filesystem"] = "fixture-independent"
        });
        void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
        {
            fields["SchemaVersion"] = 2;
            fields["GenerationId"] = storage.Provenance.GenerationId;
            fields["SessionId"] = session;
            fields["PlanSha256"] = planHash;
            fields["Provenance"] = storage.Provenance;
            fields["Sequence"] = chain.Count;
            fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
            chain.Add(Raw(fields));
        }
        foreach (var outcome in new[] { "IntentDurable", "AppliedAndVerified" })
            Add(effects, new() {
                ["Outcome"] = outcome, ["Bindings"] = leases.Bindings,
                ["BuildId"] = build, ["DerivationId"] = derivation,
                ["DescriptorSha256"] = JournalHash,
                ["ManifestSha256"] = CheckpointHash,
                ["ContentSha256"] = RetentionHash,
                ["TransportManifestSha256"] = HostHash,
                ["Evidence"] = new {
                    ObserverEvidence = observation,
                    ObserverEvidenceSha256 = Hash(Raw(observation))
                }
            });
        Unmounts((fields) => Add(sessions, fields));
        Add(sessions, new() {
            ["Action"] = "Close", ["State"] = "AppliedAndVerified",
            ["ImportResultReference"] = Reference(effects.Count - 1, effects[^1])
        });
        return new(planHash, sessions.ToImmutable(), effects.ToImmutable());
    }

    private static StageRecords Configuration(ValidatedInstallationStorage storage,
        DebianVerifiedImport predecessor, DebianCoreConfigurationPlanV1 plan,
        string planHash, InstallerBlockLeaseSet leases, Guid session)
    {
        var effects = ImmutableArray.CreateBuilder<byte[]>();
        var sessions = ImmutableArray.CreateBuilder<byte[]>();
        void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
        {
            fields["SchemaVersion"] = 3;
            fields["GenerationId"] = storage.Provenance.GenerationId;
            fields["SessionId"] = session;
            fields["OperationId"] = plan.OperationId;
            fields["PlanSha256"] = planHash;
            fields["Provenance"] = storage.Provenance;
            fields["Predecessor"] = predecessor;
            fields["Sequence"] = chain.Count;
            fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
            chain.Add(Raw(fields));
        }
        foreach (var step in new[] { "Baseline", "Files", "Debconf", "Exim", "Tls",
                 "Locale", "User", "Credential", "Sudo", "Verify" })
        {
            var observation = JsonSerializer.SerializeToElement(new SortedDictionary<string, object?> {
                ["FilesystemDeltaSha256"] = RetentionHash,
                ["MachineIdentity"] = "FirstBootPending",
                ["OperationId"] = plan.OperationId,
                ["PackageStateSha256"] = HostHash,
                ["PlanSha256"] = planHash,
                ["ProfileStep"] = step
            });
            foreach (var outcome in new[] { "IntentDurable", "AppliedAndVerified" })
                Add(effects, new() {
                    ["Step"] = step, ["Outcome"] = outcome,
                    ["Bindings"] = leases.Bindings,
                    ["ProtectedStateSha256"] = leases.ProtectedStateSha256,
                    ["Evidence"] = new {
                        ObserverEvidence = observation,
                        ObserverEvidenceSha256 = Hash(Raw(observation))
                    }
                });
        }
        Unmounts((fields) => Add(sessions, fields));
        Add(sessions, new() {
            ["Action"] = "Close", ["State"] = "AppliedAndVerified",
            ["ConfigurationResultReference"] = Reference(effects.Count - 1, effects[^1])
        });
        return new(planHash, sessions.ToImmutable(), effects.ToImmutable());
    }

    private static StageRecords Initramfs(ValidatedInstallationStorage storage,
        DebianVerifiedConfiguration predecessor, DebianLabInitramfsPlanV1 plan,
        string planHash, InstallerBlockLeaseSet leases, Guid session)
    {
        var effects = ImmutableArray.CreateBuilder<byte[]>();
        var sessions = ImmutableArray.CreateBuilder<byte[]>();
        var image = new SortedDictionary<string, object?> {
            ["Length"] = 1024L, ["Sha256"] = TargetHash
        };
        var semantics = new SortedDictionary<string, object?> {
            ["ImageLength"] = 1024L, ["ImageSha256"] = TargetHash,
            ["Path"] = "/synthetic-image"
        };
        var publication = new {
            Destination = DebianLabInitramfsPlanV1.Destination,
            Policy = "CreateNewInitramfsImageV1", Uid = 0, Gid = 0,
            Mode = 384, Candidate = image, Length = 1024L, Sha256 = TargetHash
        };
        void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
        {
            fields["SchemaVersion"] = 4;
            fields["GenerationId"] = storage.Provenance.GenerationId;
            fields["SessionId"] = session;
            fields["OperationId"] = plan.OperationId;
            fields["PlanSha256"] = planHash;
            fields["Provenance"] = storage.Provenance;
            fields["ConfigurationSha256"] = predecessor.ResultSha256;
            fields["ConfigurationCloseSha256"] = predecessor.CloseSha256;
            fields["Sequence"] = chain.Count;
            fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
            chain.Add(Raw(fields));
        }
        foreach (var step in new[] { "Baseline", "Generation", "Candidate", "Publication", "Verify" })
        {
            var observation = JsonSerializer.SerializeToElement(new SortedDictionary<string, object?> {
                ["CompleteImageQualification"] = true,
                ["ConfiguredEntriesSha256"] = plan.ConfiguredEntriesSha256,
                ["Image"] = image, ["ImageSemantics"] = semantics,
                ["OperationId"] = plan.OperationId,
                ["PackageStateSha256"] = predecessor.Observation.GetProperty("PackageStateSha256").GetString(),
                ["PlanSha256"] = planHash, ["Step"] = step
            });
            Add(effects, new() {
                ["Step"] = step, ["Outcome"] = "IntentDurable",
                ["Bindings"] = leases.Bindings,
                ["ProtectedStateSha256"] = leases.ProtectedStateSha256,
                ["Evidence"] = step == "Publication" ? new { Publication = publication } : new { }
            });
            Add(effects, new() {
                ["Step"] = step, ["Outcome"] = "AppliedAndVerified",
                ["Bindings"] = leases.Bindings,
                ["ProtectedStateSha256"] = leases.ProtectedStateSha256,
                ["Evidence"] = new {
                    ObserverEvidence = observation,
                    ObserverEvidenceSha256 = Hash(Raw(observation)),
                    HelperEvidence = new { State = "Exited", ExitCode = 0 },
                    Publication = image
                }
            });
        }
        Unmounts((fields) => Add(sessions, fields));
        Add(sessions, new() {
            ["Action"] = "Close", ["State"] = "AppliedAndVerified",
            ["InitramfsResultReference"] = Reference(effects.Count - 1, effects[^1])
        });
        return new(planHash, sessions.ToImmutable(), effects.ToImmutable());
    }

    private static void Unmounts(Action<Dictionary<string, object?>> add)
    {
        var root = new { Id = 1, Path = "/synthetic-root" };
        var payload = new { Id = 2, Path = "/synthetic-payload" };
        add(new() {
            ["Action"] = "UnmountPayload", ["State"] = "AppliedAndVerified",
            ["Evidence"] = new { Path = payload.Path,
                Before = new { Mounts = new[] { root, payload } },
                After = new { Mounts = new[] { root } } }
        });
        add(new() {
            ["Action"] = "UnmountRoot", ["State"] = "AppliedAndVerified",
            ["Evidence"] = new { Path = root.Path,
                Before = new { Mounts = new[] { root } },
                After = new { Mounts = Array.Empty<object>() } }
        });
    }

    private static void WriteChain(string directory, string plan,
        ImmutableArray<byte[]> records)
    {
        Directory.CreateDirectory(directory);
        Write(Path.Combine(directory, "plan.sha256"), Encoding.ASCII.GetBytes(plan));
        for (var i = 0; i < records.Length; i++)
            Write(Path.Combine(directory, Reference(i, records[i])), records[i]);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination,
                Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            Write(Path.Combine(destination, Path.GetRelativePath(source, file)),
                File.ReadAllBytes(file));
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None);
        output.Write(data);
        output.Flush(flushToDisk: true);
    }

    private static string Reference(int sequence, byte[] raw) =>
        $"{sequence:D8}-{Hash(raw)}.json";

    private static unsafe void RequireProtectedParent(string path)
    {
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        fixed (byte* data = buffer)
            if (Statx(-100, path, 0x100, 0x0b, data) != 0)
                throw new InvalidDataException("Fixture parent unavailable.");
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]);
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]);
        if ((mask & 0x0b) != 0x0b || uid != 0 || (mode & 0xf000) != 0x4000 ||
            (mode & 0x12) != 0)
            throw new InvalidDataException("Fixture parent is not protected.");
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial int Statx(int directory, string path, int flags,
        uint mask, byte* buffer);
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    private static byte[] Raw<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private sealed record StageRecords(string PlanHash,
        ImmutableArray<byte[]> Session, ImmutableArray<byte[]> Effects)
    {
        public string ResultHash => Hash(Effects[^1]);
        public string CloseHash => Hash(Session[^1]);
    }

    private sealed class NoEffectJournal : IDebianDeploymentJournal
    {
        public Task BeginNewAsync(Guid generation, string planSha256, CancellationToken ct) =>
            throw new InvalidOperationException("Fixture journal cannot reserve.");
        public Task<string> AppendDurablyAsync(Guid generation, ReadOnlyMemory<byte> checkpoint,
            CancellationToken ct) => throw new InvalidOperationException("Fixture journal cannot publish.");
        public Task<byte[]> ReopenAsync(string reference, CancellationToken ct) =>
            throw new InvalidOperationException("Fixture journal cannot reopen.");
    }
}
