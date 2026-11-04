using System.Collections.Immutable;
using System.Globalization;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityRecovery;

// This interpreter can only invert the explicitly planned BCD object, three EFI variables and
// RTC value. It receives the snapshot reopened from the durable boundary; it never invents a
// prior value from the plan or treats an unavailable observation as an absent variable.
internal sealed class BootRegistrationRestorer(
    CommunityBootRegistrationPlan plan, IBootRegistrationRecoveryNative native, RecoverySnapshotV1 before)
{
    private readonly List<BootRegistrationMutation> _attempted = [];
    private static readonly string[] FirmwareNames = ["BootOrder", "BootNext"];
    public bool Attempted => _attempted.Count != 0;
    public void Attempt(BootRegistrationMutation mutation)
    {
        foreach (var name in Slots())
        {
            var current = native.ReadFirmware(name);
            var priorWrite = _attempted.LastOrDefault(m => m.Kind == BootRegistrationMutationKind.Firmware && m.Slot == name);
            if (priorWrite is null ? !SameFirmware(Before(name), current) : !MatchesWritten(priorWrite, current))
                throw new InvalidOperationException($"Firmware {name} changed before the next planned operation.");
        }
        var priorRtc = _attempted.LastOrDefault(m => m.Kind == BootRegistrationMutationKind.Rtc);
        var rtc = native.ReadRtc();
        if (priorRtc is null ? !SameRtc(before.Rtc!, rtc) : !MatchesRtcWrite(priorRtc, rtc))
            throw new InvalidOperationException("RTC changed before the next planned operation.");
        _attempted.Add(mutation);
    }

    public void RequireBefore()
    {
        if (!RecoverySnapshotSerialization.VerifyHash(before) ||
            before.IndependentReadback is not { Availability: ObservationAvailability.Available, Value: true } ||
            before.Binding.Availability != ObservationAvailability.Available ||
            !RecoverySnapshotSerialization.ValueEqual(before.Binding, plan.BeforeBinding) ||
            before.Scope.Workflow != RecoveryWorkflow.CommunityDirectInstallBootRegistration ||
            !before.Scope.IncludeRtc ||
            !before.Scope.BcdMutationObjects.Order().SequenceEqual(plan.BcdSlots) ||
            !before.Scope.FirmwareMutationEntries.Order().SequenceEqual(plan.FirmwareSlots) ||
            before.Rtc is null || !SameRtc(before.Rtc, plan.BeforeRtc))
            throw new InvalidOperationException("Reopened recovery snapshot does not bind to the boot-registration plan.");
        BootRegistrationProgram.RequireGraph(plan.BeforeBcd, before.BootConfiguration);
        foreach (var name in Slots())
        {
            if (!SameFirmware(Before(name), PlannedBefore(name)) || !SameFirmware(Before(name), native.ReadFirmware(name)))
                throw new InvalidOperationException($"Reopened or current {name} state differs from the boot-registration plan.");
        }
        if (!SameRtc(before.Rtc, native.ReadRtc()))
            throw new InvalidOperationException("Current RTC state differs from the reopened recovery snapshot.");
    }

    public void RequireWritten(BootRegistrationMutation mutation)
    {
        if (mutation.Kind == BootRegistrationMutationKind.Firmware &&
            !MatchesWritten(mutation, native.ReadFirmware(mutation.Slot)))
            throw new InvalidOperationException($"Firmware {mutation.Slot} write did not produce the planned state.");
        if (mutation.Kind == BootRegistrationMutationKind.Rtc &&
            !MatchesRtcWrite(mutation, native.ReadRtc()))
            throw new InvalidOperationException("RTC write did not produce the planned state.");
    }

    public void RequireFinal()
    {
        foreach (var name in Slots())
        {
            var mutation = plan.Mutations.Single(m => m.Kind == BootRegistrationMutationKind.Firmware && m.Slot == name);
            if (!MatchesWritten(mutation, native.ReadFirmware(name)))
                throw new InvalidOperationException($"Final firmware {name} readback differs from the plan.");
        }
        var rtc = plan.Mutations.Single(m => m.Kind == BootRegistrationMutationKind.Rtc);
        if (!MatchesRtcWrite(rtc, native.ReadRtc()))
            throw new InvalidOperationException("Final RTC readback differs from the plan.");
    }

    public void Restore(BcdRecoveryGraphV1 lastConfirmedGraph)
    {
        var graph = ResolveCurrentGraph(lastConfirmedGraph, native.ReadBcd());
        // Confirm every surface before the first inverse write. An unexpected provider side
        // effect cannot be overwritten with a guessed prior value.
        foreach (var name in Slots())
        {
            var current = native.ReadFirmware(name);
            if (!SameFirmware(Before(name), current) &&
                !_attempted.Where(m => m.Kind == BootRegistrationMutationKind.Firmware && m.Slot == name)
                    .Any(m => MatchesWritten(m, current)))
                throw new InvalidOperationException($"Firmware {name} changed outside the planned transition; restoration stopped.");
        }
        var rtc = native.ReadRtc();
        if (!SameRtc(before.Rtc!, rtc) &&
            !_attempted.Where(m => m.Kind == BootRegistrationMutationKind.Rtc).Any(m => MatchesRtcWrite(m, rtc)))
            throw new InvalidOperationException("RTC changed outside the planned transition; restoration stopped.");

        foreach (var mutation in _attempted.AsEnumerable().Reverse())
        {
            switch (mutation.Kind)
            {
                case BootRegistrationMutationKind.Firmware:
                    RestoreFirmware(mutation.Slot);
                    break;
                case BootRegistrationMutationKind.Rtc:
                    RestoreRtc();
                    break;
                case BootRegistrationMutationKind.SetBcdElement:
                    // Every setter targets the newly created object, which is removed as a unit.
                    if (Guid.ParseExact(mutation.Slot[..36], "D") != plan.NewBcdObject)
                        throw new InvalidOperationException("A BCD setter targets a pre-existing object; restoration stopped.");
                    break;
                case BootRegistrationMutationKind.CreateBcdObject:
                    if (graph.Objects.Value.Any(o => o.Id == plan.NewBcdObject))
                    {
                        BootRegistrationProgram.RequireGraph(graph, native.ReadBcd());
                        RequireNoExternalInbound(graph);
                        native.DeleteBcd(plan.NewBcdObject);
                        graph = graph with { Objects = Observations.Available(graph.Objects.Value.Where(o => o.Id != plan.NewBcdObject).ToImmutableArray()) };
                        BootRegistrationProgram.RequireGraph(graph, native.ReadBcd());
                    }
                    break;
                case BootRegistrationMutationKind.DeleteBcdObject:
                    graph = RestoreStale(Guid.ParseExact(mutation.Slot, "D"), graph);
                    break;
                default: throw new InvalidOperationException("Unknown inverse boot operation.");
            }
        }
        BootRegistrationProgram.RequireGraph(before.BootConfiguration, native.ReadBcd());
        foreach (var name in Slots()) RequireFirmwareBefore(name);
        if (!SameRtc(before.Rtc!, native.ReadRtc()))
            throw new InvalidOperationException("RTC restoration readback differs from the saved state.");
    }

    private BcdRecoveryGraphV1 ResolveCurrentGraph(BcdRecoveryGraphV1 confirmed, BcdRecoveryGraphV1 current)
    {
        if (SameGraph(confirmed, current)) return confirmed;
        var last = _attempted.LastOrDefault();
        if (last is null || last.Kind is not (BootRegistrationMutationKind.CreateBcdObject or
            BootRegistrationMutationKind.DeleteBcdObject or BootRegistrationMutationKind.SetBcdElement))
            throw new InvalidOperationException("BCD changed outside the last planned operation; restoration stopped.");
        var after = ApplyGraph(confirmed, last);
        if (SameGraph(after, current)) return after;
        if (LastOperationTargetsOwnedObject(last) && ConfinedToOwnedObject(confirmed, current))
            return current;
        throw new InvalidOperationException("BCD changed outside the last planned operation; restoration stopped.");
    }

    private bool LastOperationTargetsOwnedObject(BootRegistrationMutation mutation) => mutation.Kind switch
    {
        BootRegistrationMutationKind.CreateBcdObject => mutation.Slot == plan.NewBcdObject.ToString("D"),
        BootRegistrationMutationKind.SetBcdElement => mutation.Slot.StartsWith($"{plan.NewBcdObject:D}:", StringComparison.Ordinal),
        _ => false,
    };

    private bool ConfinedToOwnedObject(BcdRecoveryGraphV1 confirmed, BcdRecoveryGraphV1 current)
    {
        if (current.Objects.Availability != ObservationAvailability.Available || current.Objects.Value.IsDefault ||
            current.Objects.Value.Select(o => o.Id).Distinct().Count() != current.Objects.Value.Length)
            return false;
        var withoutOwned = WithoutOwned(current);
        if (!SameGraph(WithoutOwned(confirmed), withoutOwned)) return false;
        var inbound = BcdMutationPlanning.InboundReferences(withoutOwned, plan.NewBcdObject);
        return inbound is { Availability: ObservationAvailability.Available } && inbound.Value.IsEmpty;
    }

    private void RequireNoExternalInbound(BcdRecoveryGraphV1 graph)
    {
        var inbound = BcdMutationPlanning.InboundReferences(WithoutOwned(graph), plan.NewBcdObject);
        if (inbound.Availability != ObservationAvailability.Available || !inbound.Value.IsEmpty)
            throw new InvalidOperationException("The new BCD object has an external reference; restoration stopped.");
    }

    private BcdRecoveryGraphV1 WithoutOwned(BcdRecoveryGraphV1 graph) => graph with
    {
        Objects = Observations.Available(graph.Objects.Value.Where(o => o.Id != plan.NewBcdObject).ToImmutableArray()),
    };

    private static BcdRecoveryGraphV1 ApplyGraph(BcdRecoveryGraphV1 graph, BootRegistrationMutation mutation)
    {
        var objects = graph.Objects.Value;
        switch (mutation.Kind)
        {
            case BootRegistrationMutationKind.CreateBcdObject:
                return graph with { Objects = Observations.Available(objects.Add(new(
                    Guid.ParseExact(mutation.Slot, "D"), checked((uint)((BcdIntegerValue)mutation.BcdValue!).Number),
                    Observations.Available<ImmutableArray<BcdElementSnapshot>>([])))) };
            case BootRegistrationMutationKind.DeleteBcdObject:
                return graph with { Objects = Observations.Available(objects.Where(o => o.Id != Guid.ParseExact(mutation.Slot, "D")).ToImmutableArray()) };
            case BootRegistrationMutationKind.SetBcdElement:
                var id = Guid.ParseExact(mutation.Slot[..36], "D");
                var type = uint.Parse(mutation.Slot.AsSpan(37), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return graph with { Objects = Observations.Available(objects.Select(o => o.Id == id ? o with
                { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != type)
                    .Append(new(type, Observations.Available(mutation.BcdValue!))).OrderBy(e => e.Type).ToImmutableArray()) } : o).ToImmutableArray()) };
            default: throw new InvalidOperationException("Not a BCD operation.");
        }
    }

    private BcdRecoveryGraphV1 RestoreStale(Guid id, BcdRecoveryGraphV1 graph)
    {
        if (graph.Objects.Value.Any(o => o.Id == id)) return graph;
        var original = before.BootConfiguration.Objects.Value.Single(o => o.Id == id);
        if (original.ObjectType != BcdRecoveryRoles.WindowsBootManagerType ||
            original.Elements.Value.Any(e => !CommunityBootRegistrationPlan.CanRestore(e)))
            throw new InvalidOperationException("A deleted BCD object has no lossless inverse.");
        native.CreateBcd(id, original.ObjectType);
        var restored = new BcdObjectSnapshot(id, original.ObjectType, Observations.Available<ImmutableArray<BcdElementSnapshot>>([]));
        graph = graph with { Objects = Observations.Available(graph.Objects.Value.Add(restored)) };
        BootRegistrationProgram.RequireGraph(graph, native.ReadBcd());
        foreach (var element in original.Elements.Value.OrderBy(e => e.Type))
        {
            var value = CommunityBootRegistrationPlan.RestoreValue(element);
            native.SetBcd(id, element.Type, value);
            restored = restored with { Elements = Observations.Available(restored.Elements.Value.Add(new(element.Type, Observations.Available(value)))) };
            graph = graph with { Objects = Observations.Available(graph.Objects.Value.Select(o => o.Id == id ? restored : o).ToImmutableArray()) };
            BootRegistrationProgram.RequireGraph(graph, native.ReadBcd());
        }
        return graph;
    }

    private void RestoreFirmware(string name)
    {
        if (SameFirmware(Before(name), native.ReadFirmware(name))) return;
        var prior = Before(name);
        if (prior.Bytes.Availability == ObservationAvailability.Absent) native.DeleteFirmware(name);
        else native.WriteFirmware(name, prior.Bytes.Value, prior.VariableAttributes.Value);
        RequireFirmwareBefore(name);
    }

    private void RestoreRtc()
    {
        if (SameRtc(before.Rtc!, native.ReadRtc())) return;
        var prior = before.Rtc!.RealTimeIsUniversal;
        if (prior.Availability == ObservationAvailability.Absent) native.DeleteRtc();
        else native.WriteRtc(prior.Value.RawData);
        if (!SameRtc(before.Rtc, native.ReadRtc()))
            throw new InvalidOperationException("RTC restoration readback differs from the saved state.");
    }

    private void RequireFirmwareBefore(string name)
    {
        if (!SameFirmware(Before(name), native.ReadFirmware(name)))
            throw new InvalidOperationException($"Firmware {name} restoration readback differs from the saved state.");
    }

    private IEnumerable<string> Slots() => FirmwareNames.Append($"Boot{plan.NewBootEntry:X4}");
    private FirmwareVariableV1 Before(string name) => name switch
    {
        "BootOrder" => before.Firmware.BootOrderRaw,
        "BootNext" => before.Firmware.BootNextRaw,
        _ when name == $"Boot{plan.NewBootEntry:X4}" => before.Firmware.BootEntries.Single(e => e.Index == plan.NewBootEntry).Raw,
        _ => throw new InvalidOperationException("Firmware slot was not planned."),
    };
    private FirmwareVariableV1 PlannedBefore(string name) => name switch
    {
        "BootOrder" => plan.BeforeFirmware.BootOrderRaw,
        "BootNext" => plan.BeforeFirmware.BootNextRaw,
        _ when name == $"Boot{plan.NewBootEntry:X4}" => plan.BeforeFirmware.BootEntries.Single(e => e.Index == plan.NewBootEntry).Raw,
        _ => throw new InvalidOperationException("Firmware slot was not planned."),
    };
    internal static bool MatchesWritten(BootRegistrationMutation mutation, FirmwareVariableV1 observed) =>
        observed.Bytes.Availability == ObservationAvailability.Available &&
        observed.VariableAttributes.Availability == ObservationAvailability.Available &&
        observed.VariableAttributes.Value == mutation.FirmwareAttributes &&
        observed.Bytes.Value.AsSpan().SequenceEqual(mutation.Bytes.AsSpan());
    internal static bool SameFirmware(FirmwareVariableV1 expected, FirmwareVariableV1 actual) =>
        expected.Bytes.Availability == actual.Bytes.Availability &&
        (expected.Bytes.Availability == ObservationAvailability.Absent ?
            expected.NativeError == 203 && actual.NativeError == 203 :
            expected.Bytes.Availability == ObservationAvailability.Available &&
            expected.VariableAttributes.Availability == ObservationAvailability.Available &&
            actual.VariableAttributes.Availability == ObservationAvailability.Available &&
            expected.VariableAttributes.Value == actual.VariableAttributes.Value &&
            expected.Bytes.Value.AsSpan().SequenceEqual(actual.Bytes.Value.AsSpan()));
    internal static bool MatchesRtcWrite(BootRegistrationMutation mutation, RtcRegistryStateV1 observed) =>
        observed.KeyPresent is { Availability: ObservationAvailability.Available, Value: true } &&
        observed.RealTimeIsUniversal.Availability == ObservationAvailability.Available &&
        observed.RealTimeIsUniversal.Value.NativeType == 11 &&
        observed.RealTimeIsUniversal.Value.RawData.AsSpan().SequenceEqual(mutation.Bytes.AsSpan());
    internal static bool SameRtc(RtcRegistryStateV1 expected, RtcRegistryStateV1 actual) =>
        expected.KeyPresent is { Availability: ObservationAvailability.Available, Value: true } &&
        actual.KeyPresent is { Availability: ObservationAvailability.Available, Value: true } &&
        expected.RealTimeIsUniversal.Availability == actual.RealTimeIsUniversal.Availability &&
        (expected.RealTimeIsUniversal.Availability == ObservationAvailability.Absent ||
            expected.RealTimeIsUniversal.Availability == ObservationAvailability.Available &&
            expected.RealTimeIsUniversal.Value.NativeType == 11 && actual.RealTimeIsUniversal.Value.NativeType == 11 &&
            expected.RealTimeIsUniversal.Value.RawData.AsSpan().SequenceEqual(actual.RealTimeIsUniversal.Value.RawData.AsSpan()));
    private static bool SameGraph(BcdRecoveryGraphV1 expected, BcdRecoveryGraphV1 actual)
    {
        try { BootRegistrationProgram.RequireGraph(expected, actual); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
