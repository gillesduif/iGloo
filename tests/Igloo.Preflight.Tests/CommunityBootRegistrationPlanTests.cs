using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class CommunityBootRegistrationPlanTests
{
    private static readonly Guid NewId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid StaleId = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void PlanningPinsStableAbsentIdentitiesAndConcreteSlotsWithoutClaimingAResolvedScope()
    {
        var evidence = Evidence();
        var first = Plan(evidence);
        var second = Plan(evidence);
        Assert.Equal((ushort)0x80, first.NewBootEntry);
        Assert.Equal(NewId, first.NewBcdObject);
        Assert.Equal(ObservationAvailability.Absent, evidence.Firmware.BootEntries.Single(e => e.Index == first.NewBootEntry).Raw.Bytes.Availability);
        Assert.DoesNotContain(evidence.Bcd.Objects.Value, o => o.Id == NewId);
        first.RequireExactMutations(second.Mutations);
        Assert.Contains(first.Mutations, m => m.Slot == "BootNext");
        Assert.Contains(first.Mutations, m => m.Slot == "BootOrder");
        Assert.Contains(first.Mutations, m => m.Slot == $"{BcdRecoveryGraphV1.FirmwareBootManagerId:D}:24000002");
        Assert.Contains(first.Mutations, m => m.Slot == CommunityBootRegistrationPlan.RtcSlot);
        Assert.Equal(ObservationAvailability.Unsupported, first.Declaration.Availability);
        Assert.Equal("BcdFirmwareSynchronizationFootprintUnproven", first.Declaration.Code);
    }

    [Fact]
    public void EquivalentCollectionOrderingDoesNotInvalidatePlanningEvidence()
    {
        var evidence = Evidence();
        var reordered = evidence with
        {
            Firmware = evidence.Firmware with { BootEntries = evidence.Firmware.BootEntries.Reverse().ToImmutableArray() },
            Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Reverse().Select(o => o with
            { Elements = Observations.Available(o.Elements.Value.Reverse().ToImmutableArray()) }).ToImmutableArray()) },
        };
        Plan(evidence).RequireUnchanged(reordered);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void FailedFirmwareReadIsNeverAnUnusedSlot(ObservationAvailability state)
    {
        var evidence = Evidence();
        var raw = new FirmwareVariableV1(Observations.Failure<ImmutableArray<byte>>(state, "test"), null,
            Observations.Failure<uint>(state, "test"));
        evidence = ReplaceEntry(evidence, EfiRecoveryParser.ParseBootEntry(0x80, raw));
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(state, result.Availability);
    }

    [Fact]
    public void ExhaustedSlotRangeNeverFallsBackToOverwritingBoot0090()
    {
        var evidence = Evidence();
        var present = evidence.Firmware.BootEntries[0].Raw;
        for (ushort i = 0x80; i < 0xff; i++) evidence = ReplaceEntry(evidence, EfiRecoveryParser.ParseBootEntry(i, present));
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal("BootPlanNoProvenFreeEntry", result.Code);
    }

    [Fact]
    public void ReferencedButAbsentSlotIsNotChosen()
    {
        var evidence = Evidence();
        var order = Variable([0, 0, 0x80, 0]);
        evidence = evidence with { Firmware = evidence.Firmware with { BootOrderRaw = order, BootOrder = EfiRecoveryParser.ParseBootOrder(order) } };
        Assert.Equal((ushort)0x81, Plan(evidence).NewBootEntry);
    }

    [Fact]
    public void PlannedBcdIdentityMustBeAbsentFromSuccessfulEnumeration()
    {
        var evidence = Evidence();
        var result = CommunityBootRegistrationPlan.Create(evidence, BcdRecoveryGraphV1.WindowsBootManagerId);
        Assert.Equal("BootPlanNewBcdObjectOccupied", result.Code);
        evidence = evidence with { Bcd = evidence.Bcd with { Objects = Observations.Failure<ImmutableArray<BcdObjectSnapshot>>(ObservationAvailability.AccessDenied, "test") } };
        Assert.Equal(ObservationAvailability.AccessDenied, CommunityBootRegistrationPlan.Create(evidence, NewId).Availability);
    }

    [Theory]
    [InlineData(0x42, "same-loader")]
    [InlineData(0x42, "different-loader")]
    [InlineData(0x42, "opaque-options")]
    [InlineData(0x142, "same-loader")]
    public void ObservedTargetEntryCannotBeDuplicatedOrSelectedByPartitionAlone(int index, string kind)
    {
        var evidence = Evidence();
        var target = evidence.Binding.Value.TargetVolume;
        var bytes = DirectInstallService.BuildEfiLoadOption(evidence.TargetPartitionNumber,
            target.OffsetBytes / target.Disk.LogicalSectorSize, target.SizeBytes / target.Disk.LogicalSectorSize,
            target.PartitionGuid, kind == "different-loader" ? @"\EFI\other\other.efi" : CommunityBootRegistrationPlan.LoaderPath,
            "Not an ownership marker").ToImmutableArray();
        if (kind == "opaque-options") bytes = bytes.AddRange(new byte[] { 1, 2, 3 });
        var entry = EfiRecoveryParser.ParseBootEntry((ushort)index, Variable(bytes));
        evidence = evidence with { Firmware = evidence.Firmware with
        { BootEntries = evidence.Firmware.BootEntries.Where(e => e.Index != index).Append(entry).ToImmutableArray() } };
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanExistingTargetEntryRequiresOwnership", result.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOrderAndOneShotPayloadPreserveBeforeStateWithoutSelectingAnExistingEntry(bool nextPresent)
    {
        var evidence = Evidence();
        var order = Variable([2, 0, 0, 0]);
        var next = nextPresent ? Variable([2, 0]) : evidence.Firmware.BootNextRaw;
        evidence = evidence with { Firmware = evidence.Firmware with
        { BootOrderRaw = order, BootOrder = EfiRecoveryParser.ParseBootOrder(order), BootNextRaw = next, BootNext = EfiRecoveryParser.ParseBootNext(next) } };
        var plan = Plan(evidence);
        var firmware = plan.Mutations.Where(m => m.Kind == BootRegistrationMutationKind.Firmware).ToArray();
        Assert.Equal(new[] { "Boot0080", "BootNext", "BootOrder" }, firmware.Select(m => m.Slot));
        Assert.Equal(new byte[] { 0x80, 0 }, firmware[1].Bytes.ToArray());
        Assert.Equal(new byte[] { 0x80, 0, 2, 0, 0, 0 }, firmware[2].Bytes.ToArray());
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, evidence.Firmware.BootOrderRaw.Bytes.Value.ToArray());
        Assert.Equal(nextPresent ? ObservationAvailability.Available : ObservationAvailability.Absent, evidence.Firmware.BootNext.Availability);
        plan.RequireUnchanged(evidence);
        // No full EFI ownership claim: live-store BCD effects and unobserved variables remain unproven.
        Assert.Equal(ObservationAvailability.Unsupported, plan.Declaration.Availability);
    }

    [Fact]
    public void NewlyObservedTargetEntryRequiresReplanningEvenWhenChosenSlotIsStillAbsent()
    {
        var evidence = Evidence(); var plan = Plan(evidence);
        var ownPayload = plan.Mutations.Single(m => m.Slot == "Boot0080").Bytes;
        var changed = ReplaceEntry(evidence, EfiRecoveryParser.ParseBootEntry(0x42, Variable(ownPayload)));
        var writes = 0;
        var boundary = new CommunityRecoveryBoundary(new NoCapture(), new NoStore());
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.ExecutePlan(plan, plan.Mutations, () => changed, () => writes++));
        Assert.Equal(CommunityRecoveryFailure.RevalidationFailed, error.Reason);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("boot-entry")]
    [InlineData("bcd-identity")]
    [InlineData("stale-set")]
    [InlineData("boot-order")]
    [InlineData("boot-next")]
    [InlineData("target")]
    [InlineData("rtc")]
    [InlineData("unavailable")]
    public void AnyChangedPrerequisiteRequiresReplanning(string change)
    {
        var evidence = Evidence();
        var plan = Plan(evidence);
        var changed = change switch
        {
            "boot-entry" => ReplaceEntry(evidence, EfiRecoveryParser.ParseBootEntry(plan.NewBootEntry, evidence.Firmware.BootEntries[0].Raw)),
            "bcd-identity" => evidence with { Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.SetItem(0, evidence.Bcd.Objects.Value[0] with { Id = NewId })) } },
            "stale-set" => AddStale(evidence),
            "boot-order" => evidence with { Firmware = evidence.Firmware with { BootOrderRaw = Variable([2, 0, 0, 0]) } },
            "boot-next" => evidence with { Firmware = evidence.Firmware with { BootNextRaw = Variable([0, 0]) } },
            "target" => evidence with { Binding = Observations.Available(evidence.Binding.Value with { TargetVolume = evidence.Binding.Value.TargetVolume with { VolumeGuid = NewId } }) },
            "rtc" => evidence with { Rtc = new(Observations.Available(true), Observations.Available(new RegistryValueV1(11, [1, 0, 0, 0, 0, 0, 0, 0]))) },
            _ => evidence with { Binding = Observations.Failure<RecoveryTargetBindingV1>(ObservationAvailability.Unavailable, "test") },
        };
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => plan.RequireUnchanged(changed));
        Assert.Equal(CommunityRecoveryFailure.RevalidationFailed, error.Reason);
    }

    [Fact]
    public void DescriptionOnlyStaleCandidateIsPinnedButNotAuthorizedForDeletion()
    {
        // Fixture contains opaque unrelated BCD state. It cannot establish that the stale
        // candidate has no inbound references, so deletion planning must now reject it.
        var result = CommunityBootRegistrationPlan.Create(AddStale(Evidence()), NewId);
        Assert.Equal(ObservationAvailability.Unsupported, result.Availability);
        Assert.Equal("BootPlanDeleteReferencesUnavailable", result.Code);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("target")]
    [InlineData("payload")]
    [InlineData("order")]
    public void CompleteSequenceIsValidatedBeforeAnyMutation(string change)
    {
        var evidence = Evidence();
        var plan = Plan(evidence);
        var proposed = change switch
        {
            "extra" => plan.Mutations.Add(plan.Mutations[0]),
            "missing" => plan.Mutations.RemoveAt(plan.Mutations.Length - 1),
            "target" => plan.Mutations.SetItem(0, plan.Mutations[0] with { Slot = "Boot0081" }),
            "payload" => plan.Mutations.SetItem(0, plan.Mutations[0] with { Bytes = [0] }),
            _ => plan.Mutations.Reverse().ToImmutableArray(),
        };
        var writes = 0;
        var boundary = new CommunityRecoveryBoundary(new NoCapture(), new NoStore());
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.ExecutePlan(plan, proposed, () => evidence, () => writes++));
        Assert.Equal(CommunityRecoveryFailure.ScopeMismatch, error.Reason);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void UnresolvedProviderFootprintCannotReachCapturePersistenceOrLegacyMutation()
    {
        var evidence = Evidence();
        var plan = Plan(evidence);
        var writes = 0;
        var boundary = new CommunityRecoveryBoundary(new NoCapture(), new NoStore());
        var error = Assert.Throws<CommunityRecoveryBoundaryException>(() => boundary.ExecutePlan(plan, plan.Mutations, () => evidence, () => writes++));
        Assert.Equal(CommunityRecoveryFailure.ScopeUnresolved, error.Reason);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("winre")]
    [InlineData("ramdisk")]
    [InlineData("optional-data")]
    public void CommunityCannotExcludeRequiredOpaqueRecoveryEvidence(string dependency)
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        snapshot = snapshot with { Scope = snapshot.Scope with { Workflow = RecoveryWorkflow.CommunityDirectInstallBootRegistration, MutationFootprintResolved = true } };
        snapshot = dependency switch
        {
            "winre" => snapshot with { WinRe = snapshot.WinRe with { Enabled = Observations.Failure<bool>(ObservationAvailability.Unsupported, "UnprovenInstallState") } },
            "ramdisk" => snapshot with { BootConfiguration = snapshot.BootConfiguration with { Objects = Observations.Available(snapshot.BootConfiguration.Objects.Value.Select(o => o.Id == snapshot.WinRe.RecoveryLoaderId.Value
                ? o with { Elements = Observations.Available(o.Elements.Value.Select(e => e.Type == 0x11000001 ? e with { QualifiedDevice = Observations.Failure<BcdDeviceValue>(ObservationAvailability.Unavailable, "provider") } : e).ToImmutableArray()) } : o).ToImmutableArray()) } },
            _ => WithOpaqueFirmware(snapshot),
        };
        var assessment = RecoverySnapshotRules.Assess(RecoverySnapshotSerialization.Seal(snapshot));
        Assert.NotEqual(BootRecoverySupport.Exact, assessment.Support);
        Assert.Contains(assessment.Issues, i => i.Code == RecoverySnapshotIssueCode.ScopeUnresolved);
        Assert.Contains(assessment.Issues, i => i.Code == (dependency == "winre" ? RecoverySnapshotIssueCode.WinReUnavailable : dependency == "ramdisk" ? RecoverySnapshotIssueCode.BcdDependency : RecoverySnapshotIssueCode.FirmwareIdentityMismatch));
    }

    private static RecoverySnapshotV1 WithOpaqueFirmware(RecoverySnapshotV1 snapshot)
    {
        var entry = snapshot.Firmware.BootEntries[0];
        var raw = entry.Raw with { Bytes = Observations.Available(entry.Raw.Bytes.Value.AddRange(new byte[] { 1, 2, 3 })) };
        return snapshot with { Firmware = snapshot.Firmware with { BootEntries = snapshot.Firmware.BootEntries.SetItem(0, EfiRecoveryParser.ParseBootEntry(0, raw)) } };
    }

    private static CommunityBootRegistrationPlan Plan(BootRegistrationEvidence evidence)
    {
        var result = CommunityBootRegistrationPlan.Create(evidence, NewId);
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        return result.Value;
    }

    private static BootRegistrationEvidence Evidence()
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var entries = Enumerable.Range(0, 256).Select(i => snapshot.Firmware.BootEntries.SingleOrDefault(e => e.Index == i) ??
            EfiRecoveryParser.ParseBootEntry((ushort)i, snapshot.Firmware.BootNextRaw)).ToImmutableArray();
        return new(snapshot.Binding, 3, snapshot.BootConfiguration, snapshot.Firmware with { BootEntries = entries }, snapshot.Rtc!);
    }

    private static BootRegistrationEvidence ReplaceEntry(BootRegistrationEvidence evidence, EfiBootEntryV1 entry) =>
        evidence with { Firmware = evidence.Firmware with { BootEntries = evidence.Firmware.BootEntries.Select(e => e.Index == entry.Index ? entry : e).ToImmutableArray() } };

    private static BootRegistrationEvidence AddStale(BootRegistrationEvidence evidence) => evidence with
    {
        Bcd = evidence.Bcd with { Objects = Observations.Available(evidence.Bcd.Objects.Value.Add(new(StaleId, 0x10100002,
            Observations.Available<ImmutableArray<BcdElementSnapshot>>([new(0x12000004, Observations.Available<BcdElementValue>(new BcdStringValue(CommunityBootRegistrationPlan.Description)))])))) },
    };
    private static FirmwareVariableV1 Variable(ImmutableArray<byte> bytes) => new(Observations.Available(bytes), 0, Observations.Available(7u));
    private sealed class NoCapture : IRecoverySnapshotCapture
    {
        public RecoverySnapshotV1 Capture(RecoveryScopeV1 scope, Guid canonicalTargetVolume) => throw new InvalidOperationException("Capture must not start.");
    }
    private sealed class NoStore : ICommunityRecoveryArtifactStore
    {
        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact) => throw new InvalidOperationException("Persistence must not start.");
        public byte[] Reopen(Guid artifactId) => throw new InvalidOperationException("Reopen must not start.");
    }
}
