using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

public sealed record BcdInboundReference(Guid ObjectId, uint ElementType);

// Pure preparation for explicit CreateObject + typed setters. No native mutation or scope approval.
public static class BcdMutationPlanning
{
    public static bool SameObject(BcdObjectSnapshot expected, BcdObjectSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return RecoverySnapshotSerialization.ValueEqual(Normalize(expected), Normalize(actual));
    }

    private static BcdObjectSnapshot Normalize(BcdObjectSnapshot obj) => obj with
    {
        Elements = obj.Elements.Availability != ObservationAvailability.Available ? obj.Elements :
            Observations.Available(obj.Elements.Value.OrderBy(e => e.Type).Select(e =>
                e.Value is { Availability: ObservationAvailability.Available, Value: BcdDeviceElementValue ordinary } &&
                e.QualifiedDevice?.Availability == ObservationAvailability.Available &&
                !BcdDependencyAnalysis.ContainsOpaqueDevice(ordinary.Device) && BcdDependencyAnalysis.QualifiedDeviceAgrees(ordinary.Device, e.QualifiedDevice.Value)
                    ? e with { Value = Observations.Available<BcdElementValue>(new BcdDeviceElementValue(e.QualifiedDevice.Value)), QualifiedDevice = null } : e).ToImmutableArray()),
    };

    public static Observation<BcdObjectSnapshot> ReplicateBootManager(BcdObjectSnapshot source, Guid destination,
        CanonicalVolumeIdentityV1 target, string path, string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (source.Id != BcdRecoveryGraphV1.WindowsBootManagerId || source.ObjectType != BcdRecoveryRoles.WindowsBootManagerType ||
            destination == Guid.Empty || destination == source.Id || !CanonicalRecoveryIdentity.IsValid(target))
            return Fail<BcdObjectSnapshot>(ObservationAvailability.Ambiguous, "BcdCloneIdentityInvalid");
        if (source.Elements.Availability != ObservationAvailability.Available)
            return Fail<BcdObjectSnapshot>(source.Elements.Availability, "BcdCloneSourceUnavailable");
        if (source.Elements.Value.IsDefault || source.Elements.Value.Select(e => e.Type).Distinct().Count() != source.Elements.Value.Length)
            return Fail<BcdObjectSnapshot>(ObservationAvailability.Ambiguous, "BcdCloneElementsInvalid");
        var elements = ImmutableArray.CreateBuilder<BcdElementSnapshot>();
        foreach (var element in source.Elements.Value)
        {
            // Same final content as copy /d + device/path replacement + delete locale/inherit.
            // Omitted values are never transiently written to the new object.
            if (element.Type is 0x11000001 or 0x12000002 or 0x12000004 or 0x12000005 or 0x14000006) continue;
            if (element.Value.Availability != ObservationAvailability.Available)
                return Fail<BcdObjectSnapshot>(element.Value.Availability, "BcdCloneElementUnavailable");
            var value = element.Value.Value;
            if (value is BcdDeviceElementValue ordinary && element.QualifiedDevice is not null)
            {
                if (element.QualifiedDevice.Availability != ObservationAvailability.Available)
                    return Fail<BcdObjectSnapshot>(element.QualifiedDevice.Availability, "BcdCloneQualificationUnavailable");
                if (!BcdDependencyAnalysis.QualifiedDeviceAgrees(ordinary.Device, element.QualifiedDevice.Value))
                    return Fail<BcdObjectSnapshot>(ObservationAvailability.Ambiguous, "BcdCloneQualificationMismatch");
                value = new BcdDeviceElementValue(element.QualifiedDevice.Value);
            }
            if (!CanSet(element.Type, value)) return Fail<BcdObjectSnapshot>(ObservationAvailability.Unsupported, "BcdCloneElementUnsupported");
            elements.Add(new(element.Type, Observations.Available(value)));
        }
        elements.Add(new(0x11000001, Observations.Available<BcdElementValue>(new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(target.Disk.GptDiskGuid, target.PartitionGuid)))));
        elements.Add(new(0x12000002, Observations.Available<BcdElementValue>(new BcdStringValue(path))));
        elements.Add(new(0x12000004, Observations.Available<BcdElementValue>(new BcdStringValue(description))));
        return Observations.Available(new BcdObjectSnapshot(destination, source.ObjectType, Observations.Available(elements.OrderBy(e => e.Type).ToImmutableArray())));
    }

    public static bool CanSet(uint type, BcdElementValue value) => BcdDependencyAnalysis.MatchesFormat(type, value) && value switch
    {
        BcdStringValue text => text.Text is not null && !text.Text.Contains('\0', StringComparison.Ordinal),
        BcdIntegerValue or BcdBooleanValue => true,
        BcdObjectValue reference => reference.ObjectId != Guid.Empty,
        BcdObjectListValue references => !references.ObjectIds.IsDefault && !references.ObjectIds.Contains(Guid.Empty),
        BcdIntegerListValue integers => !integers.Numbers.IsDefault,
        // This documented setter has no AdditionalOptions input. Never silently drop that state.
        BcdDeviceElementValue { Device: BcdQualifiedGptPartitionDevice device } =>
            device.DiskId != Guid.Empty && device.PartitionId != Guid.Empty && device.AdditionalOptions is null,
        _ => false,
    };

    public static Observation<ImmutableArray<BcdInboundReference>> InboundReferences(BcdRecoveryGraphV1 graph, Guid target)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (target == Guid.Empty || graph.SchemaVersion != 1) return Fail<ImmutableArray<BcdInboundReference>>(ObservationAvailability.Ambiguous, "BcdReferenceIdentityInvalid");
        if (graph.Objects.Availability != ObservationAvailability.Available)
            return Fail<ImmutableArray<BcdInboundReference>>(graph.Objects.Availability, "BcdReferenceEnumerationUnavailable");
        if (graph.Objects.Value.IsDefault || graph.Objects.Value.Any(o => o.Id == Guid.Empty) || graph.Objects.Value.Select(o => o.Id).Distinct().Count() != graph.Objects.Value.Length)
            return Fail<ImmutableArray<BcdInboundReference>>(ObservationAvailability.Ambiguous, "BcdReferenceObjectsInvalid");
        var references = ImmutableArray.CreateBuilder<BcdInboundReference>();
        foreach (var obj in graph.Objects.Value)
        {
            if (obj.Elements.Availability != ObservationAvailability.Available)
                return Fail<ImmutableArray<BcdInboundReference>>(obj.Elements.Availability, "BcdReferenceElementsUnavailable");
            if (obj.Elements.Value.IsDefault || obj.Elements.Value.Select(e => e.Type).Distinct().Count() != obj.Elements.Value.Length)
                return Fail<ImmutableArray<BcdInboundReference>>(ObservationAvailability.Ambiguous, "BcdReferenceElementsInvalid");
            foreach (var element in obj.Elements.Value)
            {
                if (element.Value.Availability != ObservationAvailability.Available)
                    return Fail<ImmutableArray<BcdInboundReference>>(element.Value.Availability, "BcdReferenceValueUnavailable");
                if (!BcdDependencyAnalysis.MatchesFormat(element.Type, element.Value.Value))
                    return Fail<ImmutableArray<BcdInboundReference>>(ObservationAvailability.Ambiguous, "BcdReferenceFormatInvalid");
                var ids = References(element.Value.Value);
                if (ids.Availability != ObservationAvailability.Available)
                    return Fail<ImmutableArray<BcdInboundReference>>(ids.Availability, "BcdReferenceSemanticsUnsupported");
                if (ids.Value.Contains(target)) references.Add(new(obj.Id, element.Type));
                // A failed qualified read cannot erase ordinary AdditionalOptions. Ordinary native
                // partition paths have known no-GUID-reference structure; opaque parents do not.
                if (element.QualifiedDevice?.Availability == ObservationAvailability.Available)
                {
                    var qualified = DeviceReferences(element.QualifiedDevice.Value, 0);
                    if (qualified.Availability != ObservationAvailability.Available)
                        return Fail<ImmutableArray<BcdInboundReference>>(qualified.Availability, "BcdQualifiedReferenceUnsupported");
                    if (qualified.Value.Contains(target)) references.Add(new(obj.Id, element.Type));
                }
            }
        }
        return Observations.Available(references.Distinct().OrderBy(r => r.ObjectId).ThenBy(r => r.ElementType).ToImmutableArray());
    }

    private static Observation<ImmutableArray<Guid>> References(BcdElementValue value) => value switch
    {
        BcdObjectValue id when id.ObjectId != Guid.Empty => Observations.Available<ImmutableArray<Guid>>([id.ObjectId]),
        BcdObjectListValue list when !list.ObjectIds.IsDefault && !list.ObjectIds.Contains(Guid.Empty) => Observations.Available(list.ObjectIds),
        BcdDeviceElementValue device => DeviceReferences(device.Device, 0),
        BcdStringValue { Text: not null } or BcdIntegerValue or BcdBooleanValue => Observations.Available<ImmutableArray<Guid>>([]),
        BcdIntegerListValue list when !list.Numbers.IsDefault => Observations.Available<ImmutableArray<Guid>>([]),
        _ => Fail<ImmutableArray<Guid>>(ObservationAvailability.Unsupported, "BcdOpaqueReferences"),
    };

    private static Observation<ImmutableArray<Guid>> DeviceReferences(BcdDeviceValue device, int depth)
    {
        if (depth > 32 || device is null || device.AdditionalOptions == Guid.Empty)
            return Fail<ImmutableArray<Guid>>(ObservationAvailability.Ambiguous, "BcdDeviceReferencesInvalid");
        var own = device.AdditionalOptions is Guid id ? ImmutableArray.Create(id) : [];
        if (device is BcdQualifiedGptPartitionDevice or BcdPartitionDevice or BcdBootDevice) return Observations.Available(own);
        if (device is BcdFileDevice file && file.Kind is 3 or 4)
        {
            var parent = DeviceReferences(file.Parent, depth + 1);
            return parent.Availability == ObservationAvailability.Available ? Observations.Available(own.AddRange(parent.Value)) : parent;
        }
        return Fail<ImmutableArray<Guid>>(ObservationAvailability.Unsupported, "BcdOpaqueDeviceReferences");
    }

    private static Observation<T> Fail<T>(ObservationAvailability state, string code) => Observations.Failure<T>(state, code);
}
