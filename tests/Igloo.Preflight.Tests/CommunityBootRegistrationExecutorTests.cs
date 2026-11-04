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
    public void FirmwareProjectionReproducesTheOldGraphFailureAndCompletesWithVerifiedReconciliation()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var oldNative = SynchronizedNative(evidence, plan);
        var error = Assert.Throws<InvalidOperationException>(() =>
            new BootRegistrationProgram(plan, oldNative).Run(plan.Mutations));
        Assert.Contains("BCD state or reference graph changed", error.Message, StringComparison.Ordinal);
        Assert.Empty(oldNative.Created);

        var native = SynchronizedNative(evidence, plan);
        new BootRegistrationProgram(plan, native).Run(plan.Mutations, reconcileFirmwareProjection: true);
        Assert.Equal(new[] { NewId }, native.Created);
        Assert.Equal(new[] { "Boot0080", "BootOrder", "BootNext" }, native.FirmwareWrites.Select(m => m.Slot));
        Assert.True(BcdMutationPlanning.SameObject(plan.CreatedObject, native.Graph.Objects.Value.Single(o => o.Id == NewId)));
    }

    [Theory]
    [InlineData("unrelated-change")]
    [InlineData("wrong-target")]
    [InlineData("ignored-entry")]
    public void FirmwareReconciliationRejectsOtherChangesBeforeCreatingBcdOrSelectingRestart(string defect)
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = SynchronizedNative(evidence, plan);
        var synchronize = native.OnFirmwareWrite!;
        native.OnFirmwareWrite = (n, write) =>
        {
            synchronize(n, write);
            if (defect == "ignored-entry") n.DeleteFirmware(write.Slot);
            else
                n.Graph = n.Graph with { Objects = Observations.Available(n.Graph.Objects.Value.Select(o =>
                    o.Id == (defect == "wrong-target" ? ProjectionId : BcdRecoveryGraphV1.WindowsBootManagerId)
                        ? o with { Elements = Observations.Available(o.Elements.Value.Select(e =>
                            e.Type == 0x12000002 ? new BcdElementSnapshot(e.Type,
                                Observations.Available<BcdElementValue>(new BcdStringValue(@"\EFI\wrong.efi"))) : e).ToImmutableArray()) }
                        : o).ToImmutableArray()) };
        };
        Assert.Throws<InvalidOperationException>(() =>
            new BootRegistrationProgram(plan, native).Run(plan.Mutations, reconcileFirmwareProjection: true));
        Assert.Empty(native.Created);
        Assert.Equal(0, native.RtcWrites);
        Assert.DoesNotContain(native.FirmwareWrites, m => m.Slot is "BootNext" or "BootOrder");
    }

    [Fact]
    public void RetryReusesTheExactInterruptedEntryAndItsFirmwareProjection()
    {
        var evidence = Evidence(); var firstPlan = Plan(evidence);
        var interrupted = SynchronizedNative(evidence, firstPlan);
        Assert.Throws<InvalidOperationException>(() =>
            new BootRegistrationProgram(firstPlan, interrupted).Run(firstPlan.Mutations));
        var remainingEntry = EfiRecoveryParser.ParseBootEntry(firstPlan.NewBootEntry,
            interrupted.ReadFirmware($"Boot{firstPlan.NewBootEntry:X4}"));
        evidence = evidence with
        {
            Bcd = interrupted.Graph,
            Firmware = evidence.Firmware with { BootEntries = evidence.Firmware.BootEntries
                .Select(e => e.Index == remainingEntry.Index ? remainingEntry : e).ToImmutableArray() },
        };
        var retryPlan = Plan(evidence);
        Assert.Equal(firstPlan.NewBootEntry, retryPlan.NewBootEntry);
        Assert.Empty(retryPlan.StaleBcdObjects);
        var retry = SynchronizedNative(evidence, retryPlan);
        new BootRegistrationProgram(retryPlan, retry).Run(retryPlan.Mutations, reconcileFirmwareProjection: true);
        Assert.Single(retry.Graph.Objects.Value.Where(o => o.Id == ProjectionId));
        Assert.Equal(new[] { "Boot0080", "BootOrder", "BootNext" }, retry.FirmwareWrites.Select(m => m.Slot));
    }

    private static readonly Guid ProjectionId = new("b0b0b0b0-1111-2222-3333-444444444444");
    private static FakeNative SynchronizedNative(BootRegistrationEvidence evidence, CommunityBootRegistrationPlan plan)
    {
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc);
        native.OnFirmwareWrite = (n, write) =>
        {
            var objects = n.Graph.Objects.Value;
            if (!objects.Any(o => o.Id == ProjectionId))
                objects = objects.Add(new(ProjectionId, 0x101fffff,
                    Observations.Available(plan.CreatedObject.Elements.Value.Where(e =>
                        e.Type is 0x11000001 or 0x12000002 or 0x12000004).ToImmutableArray())));
            objects = objects.Select(o =>
            {
                if (o.Id != BcdRecoveryGraphV1.FirmwareBootManagerId) return o;
                var type = write.Slot == "BootNext" ? 0x24000002u : 0x24000001u;
                var previous = o.Elements.Value.SingleOrDefault(e => e.Type == type)?.Value.Value as BcdObjectListValue;
                var ids = previous?.ObjectIds ?? [];
                ids = write.Slot switch
                {
                    "BootNext" => [ProjectionId],
                    "BootOrder" => [ProjectionId, .. ids.Where(id => id != ProjectionId)],
                    _ => ids.Contains(ProjectionId) ? ids : ids.Add(ProjectionId),
                };
                return o with { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != type)
                    .Append(new(type, Observations.Available<BcdElementValue>(new BcdObjectListValue(ids)))).ToImmutableArray()) };
            }).ToImmutableArray();
            n.Graph = n.Graph with { Objects = Observations.Available(objects) };
        };
        return native;
    }

    [Fact]
    public void ExplicitFirmwareCallsPreserveTheExactPlannedNamesPayloadsAndAttributes()
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        new BootRegistrationProgram(plan, native).Run(plan.Mutations);
        var expected = plan.Mutations.Where(m => m.Kind == BootRegistrationMutationKind.Firmware).ToImmutableArray();
        Assert.Equal(new[] { "Boot0080", "BootOrder", "BootNext" }, native.FirmwareWrites.Select(m => m.Slot));
        Assert.True(RecoverySnapshotSerialization.ValueEqual(expected, native.FirmwareWrites.ToImmutableArray()));
        // These are explicit calls only. The fake cannot certify the live BCD provider's effects.
        Assert.Equal(ObservationAvailability.Unsupported, plan.Declaration.Availability);
        Assert.Contains(plan.Blockers, b => b.Code == "BcdFirmwareSynchronizationFootprintUnproven");
    }

    [Fact]
    public void IneffectiveFinalBcdSetterStopsBeforeBootOrderBootNextAndRtcWrites()
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd);
        var lastSetter = plan.Mutations.Last(m => m.Kind == BootRegistrationMutationKind.SetBcdElement);
        native.IgnoredBcdElementType = Convert.ToUInt32(lastSetter.Slot[37..], 16);

        Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations));
        Assert.Equal(new[] { "Boot0080" }, native.FirmwareWrites.Select(m => m.Slot));
        Assert.Equal(0, native.RtcWrites);
    }

    [Fact]
    public void RtcWriteFailureStopsBeforeBootOrderAndBootNext()
    {
        var evidence = Evidence(); var plan = Plan(evidence); var native = new FakeNative(evidence.Bcd)
        {
            FailRtcWrite = true,
        };

        Assert.Throws<IOException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations));
        Assert.Equal(new[] { "Boot0080" }, native.FirmwareWrites.Select(m => m.Slot));
        Assert.Equal(1, native.RtcWrites);
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
        Assert.Equal(1, native.Writes); // The earlier EFI entry write is not claimed rolled back.
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

    [Fact]
    public void RtcFailureRestoresTheCreatedBcdObjectAndFirmwareEntry()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc) { FailRtcWrite = true };
        var before = ReopenedSnapshot(evidence, plan);
        Assert.Throws<IOException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations, before));
        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.Equal(ObservationAvailability.Absent, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
        Assert.Equal(ObservationAvailability.Absent, native.ReadRtc().RealTimeIsUniversal.Availability);
        Assert.Contains(plan.NewBcdObject, native.Deleted);
    }

    [Fact]
    public void UnexpectedValueOnOwnedBcdObjectIsRemovedBeforeRestoringPriorState()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc)
        { CorruptOwnedBcdDescription = true };

        Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(
            plan.Mutations, ReopenedSnapshot(evidence, plan)));

        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.Contains(plan.NewBcdObject, native.Deleted);
        Assert.Equal(ObservationAvailability.Absent,
            native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
    }

    [Fact]
    public void ChangedPreexistingBcdObjectPreventsInverseOfOwnedObject()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc)
        { CorruptOwnedBcdDescription = true, AddExternalInboundOnCorrupt = true };

        var failure = Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(
            plan.Mutations, ReopenedSnapshot(evidence, plan)));

        Assert.Contains("could not be verified", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.NewBcdObject, native.Deleted);
        Assert.Equal(ObservationAvailability.Available,
            native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
    }

    [Fact]
    public void RtcFailureAlsoRecreatesADeletedSupportedStaleObject()
    {
        var evidence = WithStale(Evidence()); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc) { FailRtcWrite = true };
        Assert.Throws<IOException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations, ReopenedSnapshot(evidence, plan)));
        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.Equal(new[] { StaleId }, native.Deleted.Where(id => id == StaleId));
        Assert.Contains(StaleId, native.Created);
    }

    [Fact]
    public void RtcFailureAfterSideEffectDeletesTheValueItCreated()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc) { FailRtcAfterWrite = true };
        Assert.Throws<IOException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations, ReopenedSnapshot(evidence, plan)));
        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.Equal(ObservationAvailability.Absent, native.ReadRtc().RealTimeIsUniversal.Availability);
        Assert.Equal(ObservationAvailability.Absent, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
    }

    [Fact]
    public void IneffectiveInverseFirmwareDeleteCannotClaimRestoration()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc)
        { FailRtcWrite = true, IgnoreFirmwareDeleteSlot = $"Boot{plan.NewBootEntry:X4}" };
        var error = Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(
            plan.Mutations, ReopenedSnapshot(evidence, plan)));
        Assert.Contains("could not be verified", error.Message, StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Available, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
    }

    [Fact]
    public void AlteredReopenedSnapshotStopsBeforeAnyNativeWrite()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc);
        var invalid = ReopenedSnapshot(evidence, plan) with { CanonicalHash = new string('0', 64) };
        Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations, invalid));
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void CancellationAfterBcdSetRestoresEveryAttemptedSlot()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        using var cancel = new CancellationTokenSource();
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc);
        native.OnRead = state =>
        {
            var created = state.Graph.Objects.Value.SingleOrDefault(o => o.Id == plan.NewBcdObject);
            if (created is not null && created.Elements.Value.Length == plan.CreatedObject.Elements.Value.Length) cancel.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => new BootRegistrationProgram(plan, native).Run(
            plan.Mutations, ReopenedSnapshot(evidence, plan), cancel.Token));
        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.Equal(ObservationAvailability.Absent, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
        Assert.Equal(0, native.RtcWrites);
    }

    [Fact]
    public void FirmwareFailureAfterSideEffectRestoresPriorOrderAndRtc()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc)
        { FailFirmwareAfterWriteSlot = "BootOrder" };
        Assert.Throws<IOException>(() => new BootRegistrationProgram(plan, native).Run(plan.Mutations, ReopenedSnapshot(evidence, plan)));
        BootRegistrationProgram.RequireGraph(evidence.Bcd, native.ReadBcd());
        Assert.True(native.ReadFirmware("BootOrder").Bytes.Value.AsSpan().SequenceEqual(evidence.Firmware.BootOrderRaw.Bytes.Value.AsSpan()));
        Assert.Equal(ObservationAvailability.Absent, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
        Assert.Equal(ObservationAvailability.Absent, native.ReadRtc().RealTimeIsUniversal.Availability);
    }

    [Fact]
    public void UnexpectedFirmwareSideEffectPreventsEveryInverseWrite()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var native = new FakeNative(evidence.Bcd, evidence.Firmware, evidence.Rtc)
        { UnexpectedOrderAfterEntryWrite = true, FailRtcWrite = true };
        var error = Assert.Throws<InvalidOperationException>(() => new BootRegistrationProgram(plan, native).Run(
            plan.Mutations, ReopenedSnapshot(evidence, plan)));
        Assert.Contains("could not be verified", error.Message, StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Available, native.ReadFirmware($"Boot{plan.NewBootEntry:X4}").Bytes.Availability);
        Assert.DoesNotContain(plan.NewBcdObject, native.Deleted);
    }

    [Fact]
    public void StaleObjectWithoutALosslessSetterIsRejectedDuringPlanning()
    {
        var evidence = WithStale(Evidence());
        evidence = evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Select(o => o.Id == StaleId ? o with
        { Elements = Observations.Available(o.Elements.Value.Add(new(0x11000001,
            Observations.Available<BcdElementValue>(new BcdDeviceElementValue(new BcdPartitionDevice(@"\\Device\\HarddiskVolume1")))))) } : o).ToImmutableArray()) } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanStaleRestoreUnsupported", result.Code);
    }

    [Fact]
    public void RtcValueWithoutAnExactQwordInverseIsRejectedDuringPlanning()
    {
        var evidence = Evidence() with { Rtc = new(Observations.Available(true),
            Observations.Available(new RegistryValueV1(4, [1, 0, 0, 0]))) };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanRtcRestoreUnsupported", result.Code);
    }

    private static RecoverySnapshotV1 ReopenedSnapshot(BootRegistrationEvidence evidence, CommunityBootRegistrationPlan plan)
    {
        var seed = CommunityRecoveryFixture.Exact();
        return RecoverySnapshotSerialization.Seal(seed with
        {
            Scope = new(1, RecoveryWorkflow.CommunityDirectInstallBootRegistration, false, true, 0, plan.BcdSlots, plan.FirmwareSlots),
            Binding = evidence.Binding, BootConfiguration = evidence.Bcd, Firmware = evidence.Firmware, Rtc = evidence.Rtc,
        });
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

    private sealed class FakeNative(BcdRecoveryGraphV1 graph, FirmwareSnapshotV1? firmware = null, RtcRegistryStateV1? rtc = null)
        : IBootRegistrationRecoveryNative
    {
        public BcdRecoveryGraphV1 Graph { get; set; } = graph;
        private readonly Dictionary<string, FirmwareVariableV1> _firmware = firmware is null ? [] :
            firmware.BootEntries.ToDictionary(e => $"Boot{e.Index:X4}", e => e.Raw)
                .Concat([new KeyValuePair<string, FirmwareVariableV1>("BootOrder", firmware.BootOrderRaw),
                    new("BootNext", firmware.BootNextRaw)]).ToDictionary(e => e.Key, e => e.Value);
        private RtcRegistryStateV1? _rtc = rtc;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public List<Guid> Created { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public List<BootRegistrationMutation> FirmwareWrites { get; } = [];
        public uint? IgnoredBcdElementType { get; set; }
        public bool CorruptOwnedBcdDescription { get; set; }
        public bool AddExternalInboundOnCorrupt { get; set; }
        public int RtcWrites { get; private set; }
        public bool FailRtcWrite { get; set; }
        public bool FailRtcAfterWrite { get; set; }
        public bool UnexpectedOrderAfterEntryWrite { get; set; }
        public string? FailFirmwareAfterWriteSlot { get; set; }
        public string? IgnoreFirmwareDeleteSlot { get; set; }
        public Action<FakeNative>? OnRead { get; set; }
        public Action<FakeNative, BootRegistrationMutation>? OnFirmwareWrite { get; set; }
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
            if (IgnoredBcdElementType == type) return;
            if (CorruptOwnedBcdDescription && type == 0x12000004)
            {
                value = new BcdStringValue("provider-returned-wrong-value");
                if (AddExternalInboundOnCorrupt) Graph = AddReference(Graph, id);
            }
            Graph = Graph with { Objects = Observations.Available(Graph.Objects.Value.Select(o => o.Id == id ? o with
            { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != type).Append(new(type, Observations.Available(value))).ToImmutableArray()) } : o).ToImmutableArray()) };
        }
        public void WriteFirmware(string name, ImmutableArray<byte> bytes, uint attributes)
        {
            Assert.Equal(7u, attributes);
            FirmwareWrites.Add(new(BootRegistrationMutationKind.Firmware, name, bytes, FirmwareAttributes: attributes));
            Writes++;
            _firmware[name] = new(Observations.Available(bytes), 0, Observations.Available(attributes));
            OnFirmwareWrite?.Invoke(this, FirmwareWrites[^1]);
            if (UnexpectedOrderAfterEntryWrite && name.StartsWith("Boot0", StringComparison.Ordinal))
                _firmware["BootOrder"] = new(Observations.Available<ImmutableArray<byte>>([0xff, 0xff]), 0, Observations.Available(7u));
            if (name == FailFirmwareAfterWriteSlot)
            {
                FailFirmwareAfterWriteSlot = null;
                throw new IOException("Firmware write failed after side effect.");
            }
        }
        public FirmwareVariableV1 ReadFirmware(string name) => _firmware[name];
        public void DeleteFirmware(string name)
        {
            Writes++;
            if (name == IgnoreFirmwareDeleteSlot) return;
            _firmware[name] = new(Observations.Failure<ImmutableArray<byte>>(ObservationAvailability.Absent, "test"), 203,
                Observations.Failure<uint>(ObservationAvailability.Absent, "test"));
        }
        public void WriteRtc(ImmutableArray<byte> bytes)
        {
            Assert.Equal(8, bytes.Length);
            Writes++; RtcWrites++;
            if (FailRtcWrite) throw new IOException("RTC write failed.");
            _rtc = new(Observations.Available(true), Observations.Available(new RegistryValueV1(11, bytes)));
            if (FailRtcAfterWrite)
            {
                FailRtcAfterWrite = false;
                throw new IOException("RTC write failed after side effect.");
            }
        }
        public RtcRegistryStateV1 ReadRtc() => _rtc ?? throw new InvalidOperationException("RTC fixture missing.");
        public void DeleteRtc()
        {
            Writes++;
            _rtc = new(Observations.Available(true), Observations.Failure<RegistryValueV1>(ObservationAvailability.Absent, "test"));
        }
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
