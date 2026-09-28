using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityRecovery;

internal enum BootRegistrationMutationKind { Firmware, CreateBcdObject, DeleteBcdObject, SetBcdElement, Rtc }

// This is an operation intent, not a native command. In particular CreateBcdObject must NOT
// be implemented with /copy: that command cannot accept the planned destination identity.
internal sealed record BootRegistrationMutation(BootRegistrationMutationKind Kind, string Slot,
    ImmutableArray<byte> Bytes, BcdElementValue? BcdValue = null, uint? FirmwareAttributes = null);

internal sealed record BootRegistrationEvidence(Observation<RecoveryTargetBindingV1> Binding,
    uint TargetPartitionNumber, BcdRecoveryGraphV1 Bcd, FirmwareSnapshotV1 Firmware, RtcRegistryStateV1 Rtc);

internal sealed record BootRegistrationBlocker(ObservationAvailability Availability, string Code);

// Private construction prevents callers from upgrading an incomplete footprint by setting a flag.
// Known slots are useful for review/replanning, but are NOT the complete Windows provider footprint.
internal sealed class CommunityBootRegistrationPlan
{
    internal const string Description = "iGloo distribution installer";
    internal const string LoaderPath = @"\EFI\BOOT\BOOTX64.EFI";
    internal const string RtcSlot = @"HKLM64\SYSTEM\CurrentControlSet\Control\TimeZoneInformation\RealTimeIsUniversal";
    private readonly BootRegistrationEvidence _evidence;

    private CommunityBootRegistrationPlan(BootRegistrationEvidence evidence, ushort entry, Guid bcdId,
        ImmutableArray<Guid> staleIds, ImmutableArray<BootRegistrationMutation> mutations,
        ImmutableArray<BootRegistrationBlocker> blockers, BcdObjectSnapshot createdObject)
    {
        Target = evidence.Binding.Value.TargetVolume;
        NewBootEntry = entry;
        NewBcdObject = bcdId;
        StaleBcdObjects = staleIds;
        Mutations = mutations;
        Blockers = blockers;
        _evidence = Normalize(evidence);
        CreatedObject = createdObject;
        // Deliberately unresolved: provider-created firmware identities and deletion side effects
        // have no supported binding yet. No generic WindowsBootConfiguration scope substitution.
        BcdSlots = mutations.Where(m => m.Kind is BootRegistrationMutationKind.CreateBcdObject or BootRegistrationMutationKind.DeleteBcdObject or BootRegistrationMutationKind.SetBcdElement)
            .Select(m => Guid.ParseExact(m.Slot[..36], "D")).Distinct().Order().ToImmutableArray();
        FirmwareSlots = [entry]; // Existing entries are read prerequisites, never owned write slots.
    }

    public CanonicalVolumeIdentityV1 Target { get; }
    public ushort NewBootEntry { get; }
    public Guid NewBcdObject { get; }
    public ImmutableArray<Guid> StaleBcdObjects { get; }
    public ImmutableArray<BootRegistrationMutation> Mutations { get; }
    public ImmutableArray<BootRegistrationBlocker> Blockers { get; }
    public ImmutableArray<Guid> BcdSlots { get; }
    public ImmutableArray<ushort> FirmwareSlots { get; }
    public BcdObjectSnapshot CreatedObject { get; }
    internal BcdRecoveryGraphV1 BeforeBcd => _evidence.Bcd;

    public Observation<CommunityBootRecoveryRequest> Declaration =>
        Observations.Failure<CommunityBootRecoveryRequest>(ObservationAvailability.Unsupported, Blockers[0].Code);

    public static Observation<CommunityBootRegistrationPlan> Create(BootRegistrationEvidence evidence, Guid newBcdId)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Binding.Availability != ObservationAvailability.Available)
            return Failure(evidence.Binding.Availability, "BootPlanBindingUnavailable");
        var target = evidence.Binding.Value.TargetVolume;
        if (!CanonicalRecoveryIdentity.IsValid(target) || !CanonicalRecoveryIdentity.IsValid(evidence.Binding.Value.WindowsVolume) ||
            evidence.TargetPartitionNumber == 0 || newBcdId == Guid.Empty)
            return Failure(ObservationAvailability.Ambiguous, "BootPlanIdentityInvalid");
        if (evidence.Rtc.KeyPresent.Availability != ObservationAvailability.Available)
            return Failure(evidence.Rtc.KeyPresent.Availability, "BootPlanRtcKeyUnavailable");
        if (!evidence.Rtc.KeyPresent.Value) return Failure(ObservationAvailability.Unsupported, "BootPlanRtcKeyCreationNotCovered");
        if (evidence.Rtc.RealTimeIsUniversal.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent))
            return Failure(evidence.Rtc.RealTimeIsUniversal.Availability, "BootPlanRtcValueUnavailable");
        if (evidence.Bcd.Objects.Availability != ObservationAvailability.Available)
            return Failure(evidence.Bcd.Objects.Availability, "BootPlanBcdEnumerationFailed");
        var objects = evidence.Bcd.Objects.Value;
        if (objects.IsDefault || objects.Any(o => o.Id == Guid.Empty) || objects.Select(o => o.Id).Distinct().Count() != objects.Length)
            return Failure(ObservationAvailability.Ambiguous, "BootPlanBcdIdentityInvalid");
        if (objects.Any(o => o.Id == newBcdId)) return Failure(ObservationAvailability.Ambiguous, "BootPlanNewBcdObjectOccupied");
        if (!objects.Any(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId) ||
            !objects.Any(o => o.Id == BcdRecoveryGraphV1.FirmwareBootManagerId))
            return Failure(ObservationAvailability.Unavailable, "BootPlanManagerMissing");
        var stale = ImmutableArray.CreateBuilder<Guid>();
        foreach (var obj in objects)
        {
            if (obj.Elements.Availability != ObservationAvailability.Available)
                return Failure(obj.Elements.Availability, "BootPlanStaleSetUnavailable");
            if (obj.Elements.Value.IsDefault || obj.Elements.Value.Select(e => e.Type).Distinct().Count() != obj.Elements.Value.Length)
                return Failure(ObservationAvailability.Ambiguous, "BootPlanBcdElementsInvalid");
            var description = obj.Elements.Value.SingleOrDefault(e => e.Type == 0x12000004);
            if (description is null) continue; // Successful enumeration proves the element absent.
            if (description.Value.Availability != ObservationAvailability.Available)
                return Failure(description.Value.Availability, "BootPlanDescriptionUnavailable");
            if (description.Value.Value is not BcdStringValue text)
                return Failure(ObservationAvailability.Ambiguous, "BootPlanDescriptionInvalid");
            if (text.Text == Description) stale.Add(obj.Id);
        }
        if (stale.Contains(BcdRecoveryGraphV1.WindowsBootManagerId) || stale.Contains(BcdRecoveryGraphV1.FirmwareBootManagerId) ||
            (evidence.Bcd.CurrentLoaderId.Availability == ObservationAvailability.Available && stale.Contains(evidence.Bcd.CurrentLoaderId.Value)))
            return Failure(ObservationAvailability.Ambiguous, "BootPlanProtectedObjectDescriptionCollision");

        var copied = BcdMutationPlanning.ReplicateBootManager(objects.Single(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId),
            newBcdId, target, LoaderPath, Description);
        if (copied.Availability != ObservationAvailability.Available) return Failure(copied.Availability, copied.Code!);
        foreach (var id in stale)
        {
            var inbound = BcdMutationPlanning.InboundReferences(evidence.Bcd, id);
            if (inbound.Availability != ObservationAvailability.Available) return Failure(inbound.Availability, "BootPlanDeleteReferencesUnavailable");
            if (!inbound.Value.IsEmpty) return Failure(ObservationAvailability.Unsupported, "BootPlanDeleteRequiresReferenceRewrite");
            // The final copied object must not reintroduce a reference to a deleted candidate.
            var future = evidence.Bcd with { Objects = Observations.Available(objects.Add(copied.Value)) };
            var futureInbound = BcdMutationPlanning.InboundReferences(future, id);
            if (futureInbound.Availability != ObservationAvailability.Available || !futureInbound.Value.IsEmpty)
                return Failure(ObservationAvailability.Unsupported, "BootPlanCloneReferencesDeletedObject");
        }

        var firmware = evidence.Firmware;
        var order = EfiRecoveryParser.ParseBootOrder(firmware.BootOrderRaw);
        var next = EfiRecoveryParser.ParseBootNext(firmware.BootNextRaw);
        if (order.Availability != ObservationAvailability.Available) return Failure(order.Availability, "BootPlanOrderUnavailable");
        if (next.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent))
            return Failure(next.Availability, "BootPlanNextUnavailable");
        foreach (var variable in new[] { firmware.BootOrderRaw, firmware.BootNextRaw }.Where(v => v.Bytes.Availability == ObservationAvailability.Available))
        {
            if (variable.VariableAttributes.Availability != ObservationAvailability.Available)
                return Failure(variable.VariableAttributes.Availability, "BootPlanFirmwareAttributesUnavailable");
            if (variable.VariableAttributes.Value != 7) return Failure(ObservationAvailability.Unsupported, "BootPlanFirmwareAttributesUnsupported");
        }
        if (firmware.BootEntries.IsDefault || firmware.BootEntries.Select(e => e.Index).Distinct().Count() != firmware.BootEntries.Length)
            return Failure(ObservationAvailability.Ambiguous, "BootPlanFirmwareIdentityInvalid");
        var lookup = firmware.BootEntries.ToDictionary(e => e.Index);
        // Preserve the existing sibling-search policy's explicit 0000..00ff range. Order/Next
        // references are additional prerequisites, not an assumption that all UEFI slots were read.
        var required = Enumerable.Range(0, 256).Select(i => (ushort)i).Concat(order.Value);
        if (next.Availability == ObservationAvailability.Available) required = required.Append(next.Value);
        foreach (var index in required.Distinct())
        {
            if (!lookup.TryGetValue(index, out var observed)) return Failure(ObservationAvailability.Unavailable, "BootPlanScanIncomplete");
            var raw = EfiRecoveryParser.ParseBootEntry(index, observed.Raw);
            if (raw.LoadOption.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent))
                return Failure(raw.LoadOption.Availability, "BootPlanEntryUnavailable");
            if (raw.LoadOption.Availability == ObservationAvailability.Absent && observed.Raw.NativeError != 203)
                return Failure(ObservationAvailability.Ambiguous, "BootPlanAbsenceUnproven");
        }
        var free = Enumerable.Range(0x80, 0x7f).Select(i => (ushort)i).Where(i =>
            lookup[i].Raw.Bytes.Availability == ObservationAvailability.Absent && lookup[i].Raw.NativeError == 203 &&
            !order.Value.Contains(i) && !(next.Availability == ObservationAvailability.Available && next.Value == i)).ToArray();
        if (free.Length == 0) return Failure(ObservationAvailability.Unavailable, "BootPlanNoProvenFreeEntry");
        var entry = free[0];
        var blockers = ImmutableArray.CreateBuilder<BootRegistrationBlocker>();
        blockers.Add(new(ObservationAvailability.Unsupported, "BcdFirmwareSynchronizationFootprintUnproven"));
        if (stale.Count != 0) blockers.Add(new(ObservationAvailability.Unsupported, "StaleBcdOwnershipAndDeleteSideEffectsUnproven"));
        foreach (var observed in firmware.BootEntries)
        {
            var parsed = EfiRecoveryParser.ParseBootEntry(observed.Index, observed.Raw).LoadOption;
            if (parsed.Availability == ObservationAvailability.Absent) continue;
            if (parsed.Availability != ObservationAvailability.Available)
                return Failure(parsed.Availability, "BootPlanEntryUnavailable");
            var path = parsed.Value.GptFilePath;
            if (path.Availability != ObservationAvailability.Available)
            {
                blockers.Add(new(path.Availability, "BootPlanSiblingIdentityUnresolved"));
                continue;
            }
            if (path.Value.PartitionGuid == target.PartitionGuid)
            {
                if (path.Value.StartLba != target.OffsetBytes / target.Disk.LogicalSectorSize ||
                    path.Value.SizeLba != target.SizeBytes / target.Disk.LogicalSectorSize)
                    return Failure(ObservationAvailability.Ambiguous, "BootPlanSiblingGeometryMismatch");
                // Matching a partition does not establish ownership, executable equivalence or
                // safe optional-data semantics. Do not choose that entry as BootNext, duplicate
                // it, or silently switch to reuse. This also covers observed entries above 00ff.
                return Failure(ObservationAvailability.Unsupported, "BootPlanExistingTargetEntryRequiresOwnership");
            }
        }
        var staleIds = stale.Order().ToImmutableArray();
        var operations = ImmutableArray.CreateBuilder<BootRegistrationMutation>();
        operations.Add(new(BootRegistrationMutationKind.Firmware, $"Boot{entry:X4}",
            DirectInstallService.BuildEfiLoadOption(evidence.TargetPartitionNumber, target.OffsetBytes / target.Disk.LogicalSectorSize,
                target.SizeBytes / target.Disk.LogicalSectorSize, target.PartitionGuid, LoaderPath, Description).ToImmutableArray(), FirmwareAttributes: 7));
        operations.Add(new(BootRegistrationMutationKind.Firmware, "BootNext", Ushorts([entry]), FirmwareAttributes: 7));
        foreach (var id in staleIds) operations.Add(new(BootRegistrationMutationKind.DeleteBcdObject, id.ToString("D"), []));
        operations.Add(new(BootRegistrationMutationKind.CreateBcdObject, newBcdId.ToString("D"), [], new BcdIntegerValue(copied.Value.ObjectType)));
        foreach (var element in copied.Value.Elements.Value)
            operations.Add(new(BootRegistrationMutationKind.SetBcdElement, BcdSlot(newBcdId, element.Type), [], element.Value.Value));
        operations.Add(new(BootRegistrationMutationKind.SetBcdElement, BcdSlot(BcdRecoveryGraphV1.FirmwareBootManagerId, 0x24000002), [], new BcdObjectListValue([newBcdId])));
        operations.Add(new(BootRegistrationMutationKind.Firmware, "BootOrder", Ushorts(new[] { entry }.Concat(order.Value)), FirmwareAttributes: 7));
        operations.Add(new(BootRegistrationMutationKind.Rtc, RtcSlot, [1, 0, 0, 0, 0, 0, 0, 0])); // REG_QWORD, fixed by this operation kind.
        return Observations.Available(new CommunityBootRegistrationPlan(evidence, entry, newBcdId, staleIds, operations.ToImmutable(), blockers.ToImmutable(), copied.Value));
    }

    public void RequireUnchanged(BootRegistrationEvidence current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!RecoverySnapshotSerialization.ValueEqual(Normalize(current), _evidence))
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.RevalidationFailed);
    }

    // Validate the WHOLE proposed sequence before the first write. Checking only each individual
    // call cannot detect a missing planned call until earlier mutations have already happened.
    public void RequireExactMutations(ImmutableArray<BootRegistrationMutation> attempted)
    {
        if (attempted.IsDefault || attempted.Length != Mutations.Length ||
            !RecoverySnapshotSerialization.ValueEqual(attempted, Mutations))
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeMismatch);
    }

    public void RequireScopeSlots(RecoveryScopeV1 scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Workflow != RecoveryWorkflow.CommunityDirectInstallBootRegistration || scope.SchemaVersion != 1 || !scope.IncludeRtc ||
            scope.MutationFootprintResolved != Blockers.IsEmpty ||
            scope.BcdMutationObjects.IsDefault || scope.FirmwareMutationEntries.IsDefault ||
            !scope.BcdMutationObjects.Order().SequenceEqual(BcdSlots) || !scope.FirmwareMutationEntries.Order().SequenceEqual(FirmwareSlots))
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeMismatch);
    }

    public void RequireDeclaredMutation(BootRegistrationMutation mutation)
    {
        // Identity AND payload must be predeclared; a declared object is not a wildcard for new elements.
        if (!Mutations.Any(m => RecoverySnapshotSerialization.ValueEqual(m, mutation)))
            throw new CommunityRecoveryBoundaryException(CommunityRecoveryFailure.ScopeMismatch);
    }

    private static string BcdSlot(Guid id, uint element) => $"{id:D}:{element:X8}";
    private static ImmutableArray<byte> Ushorts(IEnumerable<ushort> values) => values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToImmutableArray();
    private static Observation<CommunityBootRegistrationPlan> Failure(ObservationAvailability state, string code) => Observations.Failure<CommunityBootRegistrationPlan>(state, code);
    private static BootRegistrationEvidence Normalize(BootRegistrationEvidence evidence)
    {
        // This stricter structural comparison covers the entire enumerated stale/sibling candidate
        // set, including objects intentionally outside the recovery semantic hash. It is not an
        // Exact assessment or recovery snapshot hash. No transient drive/disk locators participate.
        return evidence with
        {
            Bcd = evidence.Bcd with { Objects = evidence.Bcd.Objects.Availability == ObservationAvailability.Available
                ? Observations.Available(evidence.Bcd.Objects.Value.OrderBy(o => o.Id).Select(o => o with { Elements = o.Elements.Availability == ObservationAvailability.Available
                    ? Observations.Available(o.Elements.Value.OrderBy(e => e.Type).ToImmutableArray()) : o.Elements }).ToImmutableArray()) : evidence.Bcd.Objects },
            Firmware = evidence.Firmware with { BootEntries = evidence.Firmware.BootEntries.OrderBy(e => e.Index).ToImmutableArray() },
        };
    }
}
