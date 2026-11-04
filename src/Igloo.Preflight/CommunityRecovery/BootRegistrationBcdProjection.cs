using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityRecovery;

// Windows exposes NVRAM boot entries in the system BCD store. Recognize only
// the projection of this plan's exact entry after its firmware readback succeeds.
// The caller owns that readback; this class performs no native reads or writes.
internal sealed class BootRegistrationBcdProjection
{
    private const uint FirmwareApplicationType = 0x101fffff;
    private const uint DisplayOrder = 0x24000001;
    private const uint BootSequence = 0x24000002;
    private readonly CommunityBootRegistrationPlan _plan;
    private Guid? _projectionId;

    public BootRegistrationBcdProjection(CommunityBootRegistrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
        RequireShape(plan.BeforeBcd);
        var existing = plan.BeforeBcd.Objects.Value.Where(IsExactProjection).ToArray();
        if (existing.Length > 1) throw Changed($"baseline-projection-count-{existing.Length}");
        _projectionId = existing.SingleOrDefault()?.Id;
    }

    public BcdRecoveryGraphV1 Reconcile(BcdRecoveryGraphV1 expected, BcdRecoveryGraphV1 actual,
        BootRegistrationMutation lastFirmwareWrite)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(lastFirmwareWrite);
        _plan.RequireDeclaredMutation(lastFirmwareWrite);
        if (lastFirmwareWrite.Kind != BootRegistrationMutationKind.Firmware ||
            (lastFirmwareWrite.Slot != $"Boot{_plan.NewBootEntry:X4}" &&
             lastFirmwareWrite.Slot is not ("BootOrder" or "BootNext")))
            throw Changed("unplanned-firmware-write");
        RequireShape(expected);
        RequireShape(actual);
        if (actual.SchemaVersion != expected.SchemaVersion)
            throw Changed("schema-changed");
        if (!RecoverySnapshotSerialization.ValueEqual(expected.CurrentLoaderId, actual.CurrentLoaderId))
            throw Changed("current-loader-observation-changed");

        var before = expected.Objects.Value.ToDictionary(o => o.Id);
        var after = actual.Objects.Value.ToDictionary(o => o.Id);
        var added = actual.Objects.Value.Where(o => !before.ContainsKey(o.Id)).ToArray();
        var projectionId = _projectionId;
        if (added.Length > 1) throw Changed($"added-object-count-{added.Length}");
        if (added.Length == 1 && (projectionId is not null || !IsExactProjection(added[0])))
            throw Changed($"added-object-{added[0].Id:D}-type-{added[0].ObjectType:X8}-not-exact-projection");
        if (added.Length == 1) projectionId = added[0].Id;
        if (after.Count != before.Count + added.Length) throw Changed("existing-object-removed");
        if (projectionId is { } pinned &&
            (!after.TryGetValue(pinned, out var projection) || !IsExactProjection(projection)))
            throw Changed($"projection-object-{pinned:D}-changed-or-missing");

        foreach (var original in expected.Objects.Value)
        {
            if (!after.TryGetValue(original.Id, out var observed))
                throw Changed($"object-{original.Id:D}-removed");
            if (BcdMutationPlanning.SameObject(original, observed)) continue;
            if (original.Id != BcdRecoveryGraphV1.FirmwareBootManagerId ||
                projectionId is not { } id ||
                !MatchesManagerProjection(original, observed, id, lastFirmwareWrite.Slot))
                throw Changed(DescribeObjectChange(original, observed));
        }

        // Do not retain a newly observed identity if another part of the graph failed.
        _projectionId = projectionId;
        return actual;
    }

    private bool IsExactProjection(BcdObjectSnapshot observed)
    {
        if (observed.Id == Guid.Empty || observed.Id == _plan.NewBcdObject ||
            observed.ObjectType != FirmwareApplicationType ||
            observed.Elements.Availability != ObservationAvailability.Available ||
            observed.Elements.Value.IsDefault || observed.Elements.Value.Length != 3)
            return false;
        var wanted = new BcdObjectSnapshot(observed.Id, FirmwareApplicationType,
            Observations.Available<ImmutableArray<BcdElementSnapshot>>([
                new(0x11000001, Observations.Available<BcdElementValue>(
                    new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(
                        _plan.Target.Disk.GptDiskGuid, _plan.Target.PartitionGuid)))),
                new(0x12000002, Observations.Available<BcdElementValue>(
                    new BcdStringValue(_plan.PlannedLoaderPath))),
                new(0x12000004, Observations.Available<BcdElementValue>(
                    new BcdStringValue(CommunityBootRegistrationPlan.Description))),
            ]));
        return BcdMutationPlanning.SameObject(wanted, observed);
    }

    private static bool MatchesManagerProjection(BcdObjectSnapshot expected, BcdObjectSnapshot actual,
        Guid projectionId, string variable)
    {
        if (expected.ObjectType != BcdRecoveryRoles.FirmwareBootManagerType ||
            actual.ObjectType != expected.ObjectType)
            return false;
        var listType = variable == "BootNext" ? BootSequence : DisplayOrder;
        var previous = ReadList(expected, listType, allowAbsent: variable == "BootNext");
        var observed = ReadList(actual, listType, allowAbsent: false);
        if (previous is null || observed is null) return false;
        ImmutableArray<Guid> permitted;
        if (variable == "BootNext")
            permitted = [projectionId];
        else if (variable == "BootOrder")
            permitted = [projectionId, .. previous.Value.Where(id => id != projectionId)];
        else
        {
            if (previous.Value.Contains(projectionId)) return false;
            permitted = previous.Value.Add(projectionId);
        }
        if (!observed.Value.SequenceEqual(permitted)) return false;
        var projected = expected with
        {
            Elements = Observations.Available(expected.Elements.Value
                .Where(e => e.Type != listType)
                .Append(new BcdElementSnapshot(listType,
                    Observations.Available<BcdElementValue>(new BcdObjectListValue(permitted))))
                .ToImmutableArray()),
        };
        return BcdMutationPlanning.SameObject(projected, actual);
    }

    private static ImmutableArray<Guid>? ReadList(BcdObjectSnapshot value, uint type, bool allowAbsent)
    {
        var element = value.Elements.Value.SingleOrDefault(e => e.Type == type);
        if (element is null) return allowAbsent ? ImmutableArray<Guid>.Empty : null;
        return element.Value.Availability == ObservationAvailability.Available &&
            element.QualifiedDevice is null &&
            element.Value.Value is BcdObjectListValue { ObjectIds.IsDefault: false } list &&
            !list.ObjectIds.Contains(Guid.Empty) &&
            list.ObjectIds.Distinct().Count() == list.ObjectIds.Length ? list.ObjectIds : null;
    }

    private static void RequireShape(BcdRecoveryGraphV1 graph)
    {
        if (graph.SchemaVersion != BcdRecoveryGraphV1.CurrentSchemaVersion)
            throw Changed("schema-unsupported");
        if (graph.CurrentLoaderId.Availability == ObservationAvailability.Available &&
            graph.CurrentLoaderId.Value == Guid.Empty)
            throw Changed("current-loader-empty");
        if (graph.Objects.Availability != ObservationAvailability.Available || graph.Objects.Value.IsDefault)
            throw Changed("object-inventory-unavailable");
        if (graph.Objects.Value.Select(o => o.Id).Distinct().Count() != graph.Objects.Value.Length)
            throw Changed("duplicate-object-id");
        foreach (var obj in graph.Objects.Value)
        {
            if (obj.Id == Guid.Empty) throw Changed("object-id-empty");
            if (obj.Elements.Availability != ObservationAvailability.Available || obj.Elements.Value.IsDefault)
                throw Changed($"object-{obj.Id:D}-elements-unavailable");
            if (obj.Elements.Value.Select(e => e.Type).Distinct().Count() != obj.Elements.Value.Length)
                throw Changed($"object-{obj.Id:D}-duplicate-element-type");
            var failed = obj.Elements.Value.FirstOrDefault(e => e.Value.Availability != ObservationAvailability.Available);
            if (failed is not null)
                throw Changed($"object-{obj.Id:D}-element-{failed.Type:X8}-unavailable");
        }
    }

    private static string DescribeObjectChange(BcdObjectSnapshot expected, BcdObjectSnapshot actual)
    {
        var prefix = $"object-{expected.Id:D}";
        if (expected.ObjectType != actual.ObjectType) return prefix + "-type-changed";
        foreach (var element in expected.Elements.Value)
        {
            var observed = actual.Elements.Value.SingleOrDefault(e => e.Type == element.Type);
            if (observed is null) return $"{prefix}-element-{element.Type:X8}-removed";
            if (!BcdMutationPlanning.SameObject(
                    expected with { Elements = Observations.Available<ImmutableArray<BcdElementSnapshot>>([element]) },
                    actual with { Elements = Observations.Available<ImmutableArray<BcdElementSnapshot>>([observed]) }))
                return $"{prefix}-element-{element.Type:X8}-changed";
        }
        var added = actual.Elements.Value.FirstOrDefault(e => expected.Elements.Value.All(p => p.Type != e.Type));
        return added is null ? prefix + "-changed" : $"{prefix}-element-{added.Type:X8}-added";
    }

    private static InvalidOperationException Changed(string reason) =>
        new($"BCD changes do not match the installer firmware entry ({reason}); execution stopped.");
}
