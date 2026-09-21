using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

// BCD element type numbers and object GUIDs are authority; localized bcdedit text is not.
// Object and integer lists retain their order (display order and inheritance can be semantic).
public sealed record BcdRecoveryGraphV1(int SchemaVersion,
    Observation<ImmutableArray<BcdObjectSnapshot>> Objects,
    Observation<Guid> CurrentLoaderId)
{
    public const int CurrentSchemaVersion = 1;
    public static readonly Guid WindowsBootManagerId = new("9dea862c-5cdd-4e70-acc1-f32b344d4795");
    public static readonly Guid FirmwareBootManagerId = new("a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba");
}

public sealed record BcdObjectSnapshot(Guid Id, uint ObjectType,
    Observation<ImmutableArray<BcdElementSnapshot>> Elements);

// QualifiedDevice is an additional observation, not a replacement that erases the ordinary read
// or a failed GetElementWithFlags. In particular CIM general failure 1 is Unavailable, not Absent.
public sealed record BcdElementSnapshot(uint Type, Observation<BcdElementValue> Value,
    Observation<BcdDeviceValue>? QualifiedDevice = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BcdStringValue), "string")]
[JsonDerivedType(typeof(BcdIntegerValue), "integer")]
[JsonDerivedType(typeof(BcdBooleanValue), "boolean")]
[JsonDerivedType(typeof(BcdObjectValue), "object")]
[JsonDerivedType(typeof(BcdObjectListValue), "objects")]
[JsonDerivedType(typeof(BcdIntegerListValue), "integers")]
[JsonDerivedType(typeof(BcdDeviceElementValue), "device")]
[JsonDerivedType(typeof(BcdOpaqueValue), "opaque")]
public abstract record BcdElementValue;
public sealed record BcdStringValue(string Text) : BcdElementValue;
public sealed record BcdIntegerValue(ulong Number) : BcdElementValue;
public sealed record BcdBooleanValue(bool Boolean) : BcdElementValue;
public sealed record BcdObjectValue(Guid ObjectId) : BcdElementValue;
public sealed record BcdObjectListValue(ImmutableArray<Guid> ObjectIds) : BcdElementValue;
public sealed record BcdIntegerListValue(ImmutableArray<ulong> Numbers) : BcdElementValue;
public sealed record BcdDeviceElementValue(BcdDeviceValue Device) : BcdElementValue;
// ProviderEvidence is diagnostic text only. Raw bytes are retained when the provider exposes them;
// even lossless opaque bytes cannot prove dependency closure or canonical device identity.
public sealed record BcdOpaqueValue(string ProviderClass, Observation<ImmutableArray<byte>> RawData,
    string? ProviderEvidence = null) : BcdElementValue;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BcdQualifiedGptPartitionDevice), "qualifiedGptPartition")]
[JsonDerivedType(typeof(BcdPartitionDevice), "nativePartition")]
[JsonDerivedType(typeof(BcdFileDevice), "file")]
[JsonDerivedType(typeof(BcdBootDevice), "boot")]
[JsonDerivedType(typeof(BcdOpaqueDevice), "opaque")]
public abstract record BcdDeviceValue(uint DeviceType, Guid? AdditionalOptions);
public sealed record BcdQualifiedGptPartitionDevice(Guid DiskId, Guid PartitionId,
    Guid? Options = null) : BcdDeviceValue(6, Options);
public sealed record BcdPartitionDevice(string NativePath,
    Guid? Options = null) : BcdDeviceValue(2, Options);
public sealed record BcdFileDevice(uint Kind, string Path, BcdDeviceValue Parent,
    Guid? Options = null) : BcdDeviceValue(Kind, Options);
public sealed record BcdBootDevice(Guid? Options = null) : BcdDeviceValue(1, Options);
public sealed record BcdOpaqueDevice(uint Kind, Observation<ImmutableArray<byte>> RawData, string ProviderClass,
    Guid? Options = null, string? ProviderEvidence = null) : BcdDeviceValue(Kind, Options);

public enum BcdDependencyIssueCode
{
    SchemaUnsupported, EnumerationFailed, InvalidRoot, DuplicateObject, MissingObject,
    ElementEnumerationFailed, DuplicateElement, ElementUnavailable, InvalidElement,
    OpaqueRelevantElement, NonCanonicalDevice, InvalidDevice, CurrentLoaderUnavailable,
}

public sealed record BcdDependencyIssue(BcdDependencyIssueCode Code,
    ObservationAvailability Availability, Guid? ObjectId = null, uint? ElementType = null);

public sealed record BcdDependencyClosure(ImmutableArray<Guid> RequiredObjectIds,
    ImmutableArray<Guid> ExcludedObjectIds, ImmutableArray<BcdDependencyIssue> Issues)
{
    public bool CanRepresentExactly => Issues.IsEmpty;
}

public static class BcdDependencyAnalysis
{
    // Follow the active Windows chain, inheritance and device options. Selection lists are
    // preserved as ordered values, but do not expand the closure into unmodified alternatives.
    // Explicit mutation roots always expand, including formerly unrelated application objects.
    public static BcdDependencyClosure Analyze(BcdRecoveryGraphV1 graph, IEnumerable<Guid> roots)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roots);
        var issues = ImmutableArray.CreateBuilder<BcdDependencyIssue>();
        var required = new HashSet<Guid>();
        if (graph.SchemaVersion != BcdRecoveryGraphV1.CurrentSchemaVersion)
            issues.Add(new(BcdDependencyIssueCode.SchemaUnsupported, ObservationAvailability.Unsupported));
        if (graph.Objects.Availability != ObservationAvailability.Available)
        {
            issues.Add(new(BcdDependencyIssueCode.EnumerationFailed, graph.Objects.Availability));
            return new([], [], issues.ToImmutable());
        }
        if (graph.Objects.Value.IsDefault)
        {
            issues.Add(new(BcdDependencyIssueCode.EnumerationFailed, ObservationAvailability.Ambiguous));
            return new([], [], issues.ToImmutable());
        }
        var lookup = graph.Objects.Value.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.ToArray());
        var queue = new Queue<Guid>(roots);
        if (queue.Count == 0) issues.Add(new(BcdDependencyIssueCode.InvalidRoot, ObservationAvailability.Ambiguous));
        while (queue.TryDequeue(out var id))
        {
            if (!required.Add(id)) continue;
            if (id == Guid.Empty)
            {
                issues.Add(new(BcdDependencyIssueCode.InvalidRoot, ObservationAvailability.Ambiguous, id));
                continue;
            }
            if (!lookup.TryGetValue(id, out var objects))
            {
                issues.Add(new(BcdDependencyIssueCode.MissingObject, ObservationAvailability.Absent, id));
                continue;
            }
            if (objects.Length != 1)
            {
                issues.Add(new(BcdDependencyIssueCode.DuplicateObject, ObservationAvailability.Ambiguous, id));
                continue;
            }
            var item = objects[0];
            if (item.ObjectType == 0) issues.Add(new(BcdDependencyIssueCode.InvalidElement, ObservationAvailability.Ambiguous, id));
            if (item.Elements.Availability != ObservationAvailability.Available || item.Elements.Value.IsDefault)
            {
                issues.Add(new(BcdDependencyIssueCode.ElementEnumerationFailed,
                    item.Elements.Availability == ObservationAvailability.Available ? ObservationAvailability.Ambiguous : item.Elements.Availability, id));
                continue;
            }
            foreach (var duplicate in item.Elements.Value.GroupBy(e => e.Type).Where(g => g.Count() > 1))
                issues.Add(new(BcdDependencyIssueCode.DuplicateElement, ObservationAvailability.Ambiguous, id, duplicate.Key));
            foreach (var element in item.Elements.Value)
            {
                if (element.Value.Availability != ObservationAvailability.Available)
                {
                    issues.Add(new(BcdDependencyIssueCode.ElementUnavailable, element.Value.Availability, id, element.Type));
                    continue;
                }
                var value = element.Value.Value;
                if (!MatchesFormat(element.Type, value))
                    issues.Add(new(BcdDependencyIssueCode.InvalidElement, ObservationAvailability.Ambiguous, id, element.Type));
                switch (value)
                {
                    case BcdObjectValue reference: queue.Enqueue(reference.ObjectId); break;
                    case BcdObjectListValue list when !list.ObjectIds.IsDefault:
                        if (list.ObjectIds.Contains(Guid.Empty)) issues.Add(new(BcdDependencyIssueCode.InvalidElement, ObservationAvailability.Ambiguous, id, element.Type));
                        if (!(element.Type is 0x24000001 or 0x24000010 &&
                            (id == BcdRecoveryGraphV1.WindowsBootManagerId || id == BcdRecoveryGraphV1.FirmwareBootManagerId)))
                            foreach (var reference in list.ObjectIds) queue.Enqueue(reference);
                        break;
                    case BcdIntegerListValue list when list.Numbers.IsDefault:
                    case BcdObjectListValue:
                        issues.Add(new(BcdDependencyIssueCode.InvalidElement, ObservationAvailability.Ambiguous, id, element.Type));
                        break;
                    case BcdDeviceElementValue device:
                        var authoritative = element.QualifiedDevice?.Availability == ObservationAvailability.Available
                            ? element.QualifiedDevice.Value : device.Device;
                        if (element.QualifiedDevice?.Availability == ObservationAvailability.Available &&
                            !QualifiedDeviceAgrees(device.Device, element.QualifiedDevice.Value))
                            issues.Add(new(BcdDependencyIssueCode.InvalidDevice, ObservationAvailability.Ambiguous, id, element.Type));
                        VisitDevice(device.Device, queue, null, id, element.Type, 0);
                        if (ContainsOpaqueDevice(device.Device))
                            issues.Add(new(BcdDependencyIssueCode.OpaqueRelevantElement, ObservationAvailability.Unsupported, id, element.Type));
                        VisitDevice(authoritative, queue, issues, id, element.Type, 0);
                        if (element.QualifiedDevice is { Availability: not ObservationAvailability.Available } failed)
                            issues.Add(new(BcdDependencyIssueCode.ElementUnavailable, failed.Availability, id, element.Type));
                        break;
                    case BcdOpaqueValue:
                        issues.Add(new(BcdDependencyIssueCode.OpaqueRelevantElement, ObservationAvailability.Unsupported, id, element.Type));
                        break;
                    case BcdStringValue text when text.Text is null:
                        issues.Add(new(BcdDependencyIssueCode.InvalidElement, ObservationAvailability.Ambiguous, id, element.Type));
                        break;
                }
            }
        }
        return new(required.Order().ToImmutableArray(), lookup.Keys.Where(k => !required.Contains(k)).Order().ToImmutableArray(), issues.ToImmutable());
    }

    public static bool MatchesFormat(uint type, BcdElementValue value) => (type >> 24 & 0xf, value) switch
    {
        (1, BcdDeviceElementValue) or (2, BcdStringValue) or (3, BcdObjectValue) or
        (4, BcdObjectListValue) or (5, BcdIntegerValue) or (6, BcdBooleanValue) or
        (7, BcdIntegerListValue) => true,
        (_, BcdOpaqueValue) => true,
        _ => false,
    };

    internal static bool ContainsOpaqueDevice(BcdDeviceValue device, int depth = 0) => depth > 32 || device is BcdOpaqueDevice ||
        device is BcdFileDevice file && ContainsOpaqueDevice(file.Parent, depth + 1);

    // Qualification may resolve a native partition locator, but must not erase file or option state.
    internal static bool QualifiedDeviceAgrees(BcdDeviceValue ordinary, BcdDeviceValue qualified, int depth = 0) =>
        depth <= 32 && ordinary.AdditionalOptions == qualified.AdditionalOptions && (ordinary, qualified) switch
        {
            (BcdPartitionDevice, BcdQualifiedGptPartitionDevice) => true,
            (BcdQualifiedGptPartitionDevice a, BcdQualifiedGptPartitionDevice b) => a.DiskId == b.DiskId && a.PartitionId == b.PartitionId,
            (BcdFileDevice a, BcdFileDevice b) => a.Kind == b.Kind && a.Path == b.Path && QualifiedDeviceAgrees(a.Parent, b.Parent, depth + 1),
            _ => false,
        };

    private static void VisitDevice(BcdDeviceValue device, Queue<Guid> queue,
        ImmutableArray<BcdDependencyIssue>.Builder? issues, Guid objectId, uint elementType, int depth)
    {
        if (device is null || depth > 32)
        {
            issues?.Add(new(BcdDependencyIssueCode.InvalidDevice, ObservationAvailability.Ambiguous, objectId, elementType));
            return;
        }
        if (device.AdditionalOptions is Guid options) queue.Enqueue(options);
        switch (device)
        {
            case BcdQualifiedGptPartitionDevice gpt when gpt.DiskId != Guid.Empty && gpt.PartitionId != Guid.Empty: break;
            case BcdFileDevice file when file.Kind is 3 or 4 && !string.IsNullOrWhiteSpace(file.Path) && file.Path.StartsWith('\\'):
                VisitDevice(file.Parent, queue, issues, objectId, elementType, depth + 1);
                break;
            case BcdOpaqueDevice:
                issues?.Add(new(BcdDependencyIssueCode.OpaqueRelevantElement, ObservationAvailability.Unsupported, objectId, elementType));
                break;
            case BcdPartitionDevice or BcdBootDevice:
                issues?.Add(new(BcdDependencyIssueCode.NonCanonicalDevice, ObservationAvailability.Unavailable, objectId, elementType));
                break;
            default:
                issues?.Add(new(BcdDependencyIssueCode.InvalidDevice, ObservationAvailability.Ambiguous, objectId, elementType));
                break;
        }
    }
}
