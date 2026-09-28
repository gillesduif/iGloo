using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class CommunityBootRegistrationExecutorTests
{
    private static readonly Guid NewId = new("eeeeeeee-1111-2222-3333-444444444444");
    private static readonly Guid StaleId = new("dddddddd-1111-2222-3333-444444444444");

    [Fact]
    public void NativeCreateUsesThePlannedGuidAndChecksReturnedIdentityAndType()
    {
        BcdMutationCall? requested = null;
        var writer = new WindowsBcdMutationWriter(call => { requested = call; return new(true, NewId, BcdRecoveryRoles.WindowsBootManagerType); });
        writer.Create(NewId, BcdRecoveryRoles.WindowsBootManagerType);
        Assert.NotNull(requested);
        Assert.Equal("CreateObject", requested.Method);
        Assert.Equal(NewId.ToString("B"), requested.Parameters["Id"]);
        Assert.Equal(BcdRecoveryRoles.WindowsBootManagerType, requested.Parameters["Type"]);
        Assert.Equal(NewId, requested.ObjectId);
        Assert.True(requested.StoreMethod);
    }

    [Theory]
    [InlineData("generated-id")]
    [InlineData("type")]
    [InlineData("failed")]
    public void ProviderCannotSubstituteTheRequestedCreation(string defect)
    {
        var calls = 0;
        var writer = new WindowsBcdMutationWriter(_ => { calls++; return defect switch
        {
            "generated-id" => new(true, StaleId, BcdRecoveryRoles.WindowsBootManagerType),
            "type" => new(true, NewId, BcdRecoveryRoles.WindowsLoaderType),
            _ => new(false),
        }; });
        Assert.Throws<InvalidOperationException>(() => writer.Create(NewId, BcdRecoveryRoles.WindowsBootManagerType));
        Assert.Equal(1, calls); // No retry or alternative/generated identity.
    }

    [Theory]
    [InlineData("string", "SetStringElement")]
    [InlineData("integer", "SetIntegerElement")]
    [InlineData("boolean", "SetBooleanElement")]
    [InlineData("object", "SetObjectElement")]
    [InlineData("objects", "SetObjectListElement")]
    [InlineData("integers", "SetIntegerListElement")]
    [InlineData("gpt", "SetQualifiedPartitionDeviceElement")]
    public void NativeSettersCarryExactTypedPlannedValues(string kind, string method)
    {
        var target = CommunityRecoveryFixture.Exact().Binding.Value.TargetVolume;
        var (type, value) = kind switch
        {
            "string" => (0x12000002u, (BcdElementValue)new BcdStringValue(@"\EFI\BOOT\BOOTX64.EFI")),
            "integer" => (0x25000004u, new BcdIntegerValue(ulong.MaxValue)),
            "boolean" => (0x16000020u, new BcdBooleanValue(true)),
            "object" => (0x23000003u, new BcdObjectValue(StaleId)),
            "objects" => (0x24000002u, new BcdObjectListValue([StaleId, NewId])),
            "integers" => (0x17000077u, new BcdIntegerListValue([ulong.MaxValue, 1])),
            _ => (0x11000001u, new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(target.Disk.GptDiskGuid, target.PartitionGuid))),
        };
        BcdMutationCall? requested = null;
        new WindowsBcdMutationWriter(call => { requested = call; return new(true); }).Set(NewId, type, value);
        Assert.NotNull(requested);
        Assert.Equal(method, requested.Method);
        Assert.Equal(NewId, requested.ObjectId);
        Assert.Equal(type, requested.Parameters["Type"]);
        Assert.False(requested.StoreMethod);
        switch (kind)
        {
            case "string": Assert.Equal(@"\EFI\BOOT\BOOTX64.EFI", requested.Parameters["String"]); break;
            case "integer": Assert.Equal(ulong.MaxValue, requested.Parameters["Integer"]); break;
            case "boolean": Assert.Equal(true, requested.Parameters["Boolean"]); break;
            case "object": Assert.Equal(StaleId.ToString("B"), requested.Parameters["Id"]); break;
            case "objects": Assert.Equal(new[] { StaleId.ToString("B"), NewId.ToString("B") }, Assert.IsType<string[]>(requested.Parameters["Ids"])); break;
            case "integers": Assert.Equal(new[] { ulong.MaxValue, 1UL }, Assert.IsType<ulong[]>(requested.Parameters["Integers"])); break;
            default:
                Assert.Equal(1u, requested.Parameters["PartitionStyle"]);
                Assert.Equal(target.Disk.GptDiskGuid.ToString("B"), requested.Parameters["DiskSignature"]);
                Assert.Equal(target.PartitionGuid.ToString("B"), requested.Parameters["PartitionIdentifier"]);
                break;
        }
    }

    [Fact]
    public void UnsupportedSetterNeverInvokesProvider()
    {
        var calls = 0;
        var writer = new WindowsBcdMutationWriter(_ => { calls++; return new(true); });
        Assert.Throws<NotSupportedException>(() => writer.Set(NewId, 0x18000001, new BcdOpaqueValue("opaque", Observations.Available<ImmutableArray<byte>>([1]))));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FakeProgramCreatesOnlyThePlannedObjectAndMatchesFinalCopiedContent()
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        new BootRegistrationProgram(plan, native).Run(plan.Mutations);
        Assert.Equal(new[] { NewId }, native.Created);
        Assert.True(BcdMutationPlanning.SameObject(plan.CreatedObject, native.Graph.Objects.Value.Single(o => o.Id == NewId)));
        Assert.Equal(plan.Mutations.Length, native.Writes);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void ExplicitFirmwareCallsPreserveTheExactPlannedNamesPayloadsAndAttributes()
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        new BootRegistrationProgram(plan, native).Run(plan.Mutations);
        var expected = plan.Mutations.Where(m => m.Kind == BootRegistrationMutationKind.Firmware).ToImmutableArray();
        Assert.Equal(new[] { "Boot0080", "BootNext", "BootOrder" }, native.FirmwareWrites.Select(m => m.Slot));
        Assert.True(RecoverySnapshotSerialization.ValueEqual(expected, native.FirmwareWrites.ToImmutableArray()));
        // These are explicit calls only. The fake cannot certify the live BCD provider's effects.
        Assert.Equal(ObservationAvailability.Unsupported, plan.Declaration.Availability);
        Assert.Contains(plan.Blockers, b => b.Code == "BcdFirmwareSynchronizationFootprintUnproven");
    }

    [Theory]
    [InlineData("Boot0081")]
    [InlineData("BootOrder")]
    [InlineData("BootNext")]
    [InlineData("VendorUnknown")]
    public void AdditionalFirmwareInstructionIsRejectedBeforeAnyNativeCall(string variable)
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        var extra = new BootRegistrationMutation(BootRegistrationMutationKind.Firmware, variable, [1, 0], FirmwareAttributes: 7);
        Assert.Throws<CommunityRecoveryBoundaryException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations.Add(extra)));
        Assert.Equal(0, native.Reads);
        Assert.Equal(0, native.Writes);
        // Rejecting extra instructions does NOT detect implicit effects of an allowed provider call.
    }

    [Theory]
    [InlineData("Boot0080")]
    [InlineData("BootOrder")]
    [InlineData("BootNext")]
    public void ChangedFirmwarePayloadCannotHideBehindAnAlreadyDeclaredVariable(string variable)
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        var changed = plan.Mutations.Select(m => m.Slot == variable ? m with { Bytes = m.Bytes.Add(0) } : m).ToImmutableArray();
        Assert.Throws<CommunityRecoveryBoundaryException>(() => new BootRegistrationProgram(plan, native).Run(changed));
        Assert.Equal(0, native.Reads);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void CompleteEmptyInboundReferencesDoNotEstablishStaleOwnershipOrFirmwareEffects()
    {
        var evidence = WithStale(Evidence()); var plan = Plan(evidence);
        var references = BcdMutationPlanning.InboundReferences(evidence.Bcd, StaleId);
        Assert.Equal(ObservationAvailability.Available, references.Availability);
        Assert.Empty(references.Value);
        Assert.Contains(plan.Blockers, b => b.Code == "StaleBcdOwnershipAndDeleteSideEffectsUnproven");
        var captures = 0;
        var boundary = new CommunityRecoveryBoundary(new SnapshotCapture(() => { captures++; return CommunityRecoveryFixture.Exact(); }), new NoStore());
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => new CommunityBootRegistrationExecutor(boundary, () => evidence).Execute(plan));
        Assert.Equal(CommunityRecoveryFailure.ScopeUnresolved, error.Reason);
        Assert.Equal(0, captures);
    }

    [Fact]
    public void OpaqueStaleDeviceCannotProveReferenceOwnershipEvenWithAnEmptyAdditionalOptionsField()
    {
        var evidence = WithStale(Evidence());
        evidence = evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Select(o => o.Id == StaleId ? o with
        { Elements = Observations.Available(o.Elements.Value.Add(new(0x11000001, Observations.Available<BcdElementValue>(new BcdDeviceElementValue(
            new BcdOpaqueDevice(5, Observations.Available<ImmutableArray<byte>>([6, 0, 0, 0]), "BcdDeviceUnknownData")))))) } : o).ToImmutableArray()) } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanDeleteReferencesUnavailable", result.Code);
    }

    [Theory]
    [InlineData("missing-entry")]
    [InlineData("extra-entry")]
    [InlineData("duplicate-entry")]
    [InlineData("missing-rtc")]
    public void KnownScopeSlotsCannotOmitOrExpandTheProgram(string defect)
    {
        var plan = Plan(Evidence());
        var scope = new RecoveryScopeV1(1, RecoveryWorkflow.CommunityDirectInstallBootRegistration, false, true, 0, plan.BcdSlots, plan.FirmwareSlots);
        scope = defect switch
        {
            "missing-entry" => scope with { FirmwareMutationEntries = [] },
            "extra-entry" => scope with { FirmwareMutationEntries = plan.FirmwareSlots.Add(0x81) },
            "duplicate-entry" => scope with { FirmwareMutationEntries = plan.FirmwareSlots.Add(plan.NewBootEntry) },
            _ => scope with { IncludeRtc = false },
        };
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireScopeSlots(scope));
        Assert.Equal(ObservationAvailability.Unsupported, plan.Declaration.Availability);
    }

    [Fact]
    public void StaleDeletionUsesOnlyPinnedGuidAndNoForceOrCleanupParameters()
    {
        var evidence = WithStale(Evidence()); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        new BootRegistrationProgram(plan, native).Run(plan.Mutations);
        Assert.Equal(new[] { StaleId }, native.Deleted);
        BcdMutationCall? requested = null;
        new WindowsBcdMutationWriter(call => { requested = call; return new(true); }).Delete(StaleId);
        Assert.NotNull(requested);
        Assert.Equal("DeleteObject", requested.Method);
        Assert.Equal(StaleId.ToString("B"), Assert.Single(requested.Parameters).Value);
    }

    [Theory]
    [InlineData("different-description-match")]
    [InlineData("gains-reference")]
    [InlineData("changed-stale")]
    [InlineData("missing-stale")]
    public void ReopenedGraphChangesPreventEveryWrite(string change)
    {
        var evidence = WithStale(Evidence()); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        native.Graph = change switch
        {
            "different-description-match" => native.Graph with { Objects = Observations.Available(native.Graph.Objects.Value.Add(Stale() with { Id = new Guid("cccccccc-1111-2222-3333-444444444444") })) },
            "gains-reference" => AddReference(native.Graph, StaleId),
            "missing-stale" => native.Graph with { Objects = Observations.Available(native.Graph.Objects.Value.Where(o => o.Id != StaleId).ToImmutableArray()) },
            _ => native.Graph with { Objects = Observations.Available(native.Graph.Objects.Value.Select(o => o.Id == StaleId ? o with { ObjectType = BcdRecoveryRoles.WindowsLoaderType } : o).ToImmutableArray()) },
        };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations));
        Assert.Equal(0, native.Writes);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void ReferenceAddedImmediatelyBeforeDeleteStopsThatDelete()
    {
        var evidence = WithStale(Evidence()); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        native.OnRead = n => { if (n.Reads == 3) n.Graph = AddReference(n.Graph, StaleId); };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations));
        Assert.Empty(native.Deleted);
        Assert.Empty(native.Created);
        Assert.Equal(2, native.Writes); // Earlier fixture firmware writes are not claimed rolled back.
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("guid")]
    public void UnplannedOrMissingNativeInstructionFailsBeforeFirstWrite(string change)
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        var steps = change switch
        {
            "extra" => plan.Mutations.Add(plan.Mutations[0]),
            "missing" => plan.Mutations.RemoveAt(0),
            _ => plan.Mutations.Select(m => m.Kind == BootRegistrationMutationKind.CreateBcdObject ? m with { Slot = StaleId.ToString("D") } : m).ToImmutableArray(),
        };
        Assert.Throws<CommunityRecoveryBoundaryException>(() => new BootRegistrationProgram(plan, native).Run(steps));
        Assert.Equal(0, native.Writes);
        Assert.Equal(0, native.Reads);
    }

    [Theory]
    [InlineData("reference-present")]
    [InlineData("opaque-reference")]
    public void DeletionRequiringMoreSlotsOrUnknownEffectsRejectsPlanning(string defect)
    {
        var evidence = WithStale(Evidence());
        evidence = evidence with { Bcd = defect == "reference-present" ? AddReference(evidence.Bcd, StaleId) : evidence.Bcd with
        { Objects = Observations.Available(evidence.Bcd.Objects.Value.Add(new(new Guid("cccccccc-1111-2222-3333-444444444444"), 0x1010000a,
            Observations.Available<ImmutableArray<BcdElementSnapshot>>([new(0x18000001, Observations.Available<BcdElementValue>(new BcdOpaqueValue("unknown", Observations.Available<ImmutableArray<byte>>([1]))))])))) } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal(defect == "reference-present" ? "BootPlanDeleteRequiresReferenceRewrite" : "BootPlanDeleteReferencesUnavailable", result.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingOrChangingAnExistingReferenceRequiresReplanning(bool replace)
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var changed = evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Select(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId
            ? o with { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != 0x23000003).Concat(replace ? [new BcdElementSnapshot(0x23000003, Observations.Available<BcdElementValue>(new BcdObjectValue(StaleId)))] : Array.Empty<BcdElementSnapshot>()).ToImmutableArray()) } : o).ToImmutableArray()) } };
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireUnchanged(changed));
    }

    [Fact]
    public void ScopeCannotOmitAddOrSubstituteAnObjectSlot()
    {
        var plan = Plan(Evidence());
        var scope = new RecoveryScopeV1(1, RecoveryWorkflow.CommunityDirectInstallBootRegistration, false, true, 0, plan.BcdSlots, plan.FirmwareSlots);
        plan.RequireScopeSlots(scope); // Coverage only; the candidate still has no resolved declaration.
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireScopeSlots(scope with { MutationFootprintResolved = true }));
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireScopeSlots(scope with { BcdMutationObjects = [] }));
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireScopeSlots(scope with { FirmwareMutationEntries = [0x81] }));
        Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireScopeSlots(scope with { BcdMutationObjects = plan.BcdSlots.Add(StaleId) }));
        Assert.Equal(ObservationAvailability.Unsupported, plan.Declaration.Availability);
    }

    [Fact]
    public void ProductionExecutorCannotEnterNativeCodeBeforeItsBoundarySucceeds()
    {
        var evidence = Evidence(); var captures = 0;
        var boundary = new CommunityRecoveryBoundary(new SnapshotCapture(() => { captures++; return CommunityRecoveryFixture.Exact(); }), new NoStore());
        var executor = new CommunityBootRegistrationExecutor(boundary, () => evidence);
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => executor.Execute(Plan(evidence)));
        Assert.Equal(CommunityRecoveryFailure.ScopeUnresolved, error.Reason);
        Assert.Equal(0, captures); // Private Windows backend is constructed only after success.
    }

    [Fact]
    public void MissingRtcKeyCannotExpandExecutionIntoRegistryKeyCreation()
    {
        var evidence = Evidence();
        evidence = evidence with { Rtc = evidence.Rtc with { KeyPresent = Observations.Available(false) } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanRtcKeyCreationNotCovered", result.Code);
    }

    [Fact]
    public void UnsupportedRequiredSourceElementRejectsTheEntirePlan()
    {
        var evidence = Evidence();
        evidence = evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Select(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId ? o with
        { Elements = Observations.Available(o.Elements.Value.Add(new(0x18000001, Observations.Available<BcdElementValue>(new BcdOpaqueValue("unknown", Observations.Available<ImmutableArray<byte>>([1])))))) } : o).ToImmutableArray()) } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BcdCloneElementUnsupported", result.Code);
    }

    private static BootRegistrationEvidence Evidence()
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var graph = snapshot.BootConfiguration with { Objects = Observations.Available(snapshot.BootConfiguration.Objects.Value.Where(o => !o.Elements.Value.Any(e => e.Value.Value is BcdOpaqueValue)).ToImmutableArray()) };
        var entries = Enumerable.Range(0, 256).Select(i => snapshot.Firmware.BootEntries.SingleOrDefault(e => e.Index == i) ?? EfiRecoveryParser.ParseBootEntry((ushort)i, snapshot.Firmware.BootNextRaw)).ToImmutableArray();
        return new(snapshot.Binding, 3, graph, snapshot.Firmware with { BootEntries = entries }, snapshot.Rtc!);
    }
    private static CommunityBootRegistrationPlan Plan(BootRegistrationEvidence evidence)
    {
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        return result.Value;
    }
    private static BcdObjectSnapshot Stale() => new(StaleId, BcdRecoveryRoles.WindowsBootManagerType,
        Observations.Available<ImmutableArray<BcdElementSnapshot>>([new(0x12000004, Observations.Available<BcdElementValue>(new BcdStringValue(CommunityBootRegistrationPlan.Description)))]));
    private static BootRegistrationEvidence WithStale(BootRegistrationEvidence evidence) => evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Add(Stale())) } };
    private static BcdRecoveryGraphV1 AddReference(BcdRecoveryGraphV1 graph, Guid target) => graph with { Objects = Observations.Available(graph.Objects.Value.Select(o => o.Id == BcdRecoveryGraphV1.FirmwareBootManagerId ? o with
    { Elements = Observations.Available(o.Elements.Value.Add(new(0x24000002, Observations.Available<BcdElementValue>(new BcdObjectListValue([target]))))) } : o).ToImmutableArray()) };

    private sealed class FakeNative(BcdRecoveryGraphV1 graph) : IBootRegistrationNative
    {
        public BcdRecoveryGraphV1 Graph { get; set; } = graph;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public List<Guid> Created { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public List<BootRegistrationMutation> FirmwareWrites { get; } = [];
        public Action<FakeNative>? OnRead { get; set; }
        public BcdRecoveryGraphV1 ReadBcd() { Reads++; OnRead?.Invoke(this); return Graph; }
        public void CreateBcd(Guid id, uint type)
        {
            Writes++; Created.Add(id);
            Graph = Graph with { Objects = Observations.Available(Graph.Objects.Value.Add(new(id, type, Observations.Available<ImmutableArray<BcdElementSnapshot>>([])))) };
        }
        public void DeleteBcd(Guid id)
        {
            Writes++; Deleted.Add(id);
            Graph = Graph with { Objects = Observations.Available(Graph.Objects.Value.Where(o => o.Id != id).ToImmutableArray()) };
        }
        public void SetBcd(Guid id, uint type, BcdElementValue value)
        {
            Writes++;
            Graph = Graph with { Objects = Observations.Available(Graph.Objects.Value.Select(o => o.Id == id ? o with
            { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != type).Append(new(type, Observations.Available(value))).ToImmutableArray()) } : o).ToImmutableArray()) };
        }
        public void WriteFirmware(string name, ImmutableArray<byte> bytes, uint attributes)
        {
            Assert.Equal(7u, attributes);
            FirmwareWrites.Add(new(BootRegistrationMutationKind.Firmware, name, bytes, FirmwareAttributes: attributes));
            Writes++;
        }
        public void WriteRtc(ImmutableArray<byte> bytes) { Assert.Equal(8, bytes.Length); Writes++; }
    }
    private sealed class SnapshotCapture(Func<RecoverySnapshotV1> capture) : IRecoverySnapshotCapture
    {
        public RecoverySnapshotV1 Capture(RecoveryScopeV1 scope, Guid canonicalTargetVolume) => capture();
    }
    private sealed class NoStore : ICommunityRecoveryArtifactStore
    {
        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact) => throw new InvalidOperationException("Must not persist.");
        public byte[] Reopen(Guid artifactId) => throw new InvalidOperationException("Must not reopen.");
    }
}
