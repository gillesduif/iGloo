using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

public enum BcdRecoveryObjectRole
{
    FirmwareBootManager, WindowsBootManager, WindowsLoader, WindowsResume,
    DeviceOptions, InheritedSettings, SupportedObject,
}

public sealed record BcdRecoveryRoleIssue(Guid ObjectId, BcdRecoveryObjectRole Role,
    ObservationAvailability Availability, uint? ObservedType = null);

// These are packed object types, not element types. Their image/application fields are documented at:
// https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcdobject
// Microsoft's Windows-classic-samples BcdSampleLib/Constants.cs also defines the DEVICE object class.
public static class BcdRecoveryRoles
{
    public const uint FirmwareBootManagerType = 0x10100001;
    public const uint WindowsBootManagerType = 0x10100002;
    public const uint WindowsLoaderType = 0x10200003;
    public const uint WindowsResumeType = 0x10200004;
    public const uint DeviceOptionsType = 0x30000000;

    // Role validation supplements dependency completeness and canonical-device correlation. It never
    // infers a loader identity from its description, file name, default selection or ordinal position.
    public static ImmutableArray<BcdRecoveryRoleIssue> Validate(BcdRecoveryGraphV1 graph,
        IEnumerable<Guid> requiredObjectIds, Observation<Guid> configuredWinReLoaderId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(requiredObjectIds);
        ArgumentNullException.ThrowIfNull(configuredWinReLoaderId);
        var issues = ImmutableArray.CreateBuilder<BcdRecoveryRoleIssue>();
        if (graph.Objects.Availability != ObservationAvailability.Available)
            return [new(Guid.Empty, BcdRecoveryObjectRole.SupportedObject, graph.Objects.Availability)];
        if (graph.Objects.Value.IsDefault)
            return [new(Guid.Empty, BcdRecoveryObjectRole.SupportedObject, ObservationAvailability.Ambiguous)];
        var objects = graph.Objects.Value.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.ToArray());

        void Require(Guid id, BcdRecoveryObjectRole role)
        {
            if (!objects.TryGetValue(id, out var matches))
            { issues.Add(new(id, role, ObservationAvailability.Absent)); return; }
            if (matches.Length != 1 || id == Guid.Empty)
            { issues.Add(new(id, role, ObservationAvailability.Ambiguous)); return; }
            if (!Matches(matches[0].ObjectType, role))
                issues.Add(new(id, role, role == BcdRecoveryObjectRole.SupportedObject ?
                    ObservationAvailability.Unsupported : ObservationAvailability.Ambiguous, matches[0].ObjectType));
        }

        void DeviceOptions(BcdDeviceValue? device, int depth = 0)
        {
            if (device is null || depth > 32) return; // The structural validator rejects these shapes.
            if (device.AdditionalOptions is Guid id) Require(id, BcdRecoveryObjectRole.DeviceOptions);
            if (device is BcdFileDevice file) DeviceOptions(file.Parent, depth + 1);
        }

        Require(BcdRecoveryGraphV1.FirmwareBootManagerId, BcdRecoveryObjectRole.FirmwareBootManager);
        Require(BcdRecoveryGraphV1.WindowsBootManagerId, BcdRecoveryObjectRole.WindowsBootManager);
        if (graph.CurrentLoaderId.Availability == ObservationAvailability.Available)
            Require(graph.CurrentLoaderId.Value, BcdRecoveryObjectRole.WindowsLoader);
        else issues.Add(new(Guid.Empty, BcdRecoveryObjectRole.WindowsLoader, graph.CurrentLoaderId.Availability));
        if (configuredWinReLoaderId.Availability == ObservationAvailability.Available)
            Require(configuredWinReLoaderId.Value, BcdRecoveryObjectRole.WindowsLoader);

        foreach (var id in requiredObjectIds.Distinct().Order())
        {
            Require(id, BcdRecoveryObjectRole.SupportedObject);
            if (!objects.TryGetValue(id, out var matches) || matches.Length != 1) continue;
            var item = matches[0];
            if (item.Elements.Availability != ObservationAvailability.Available || item.Elements.Value.IsDefault) continue;
            foreach (var element in item.Elements.Value)
            {
                if (element.Value.Availability != ObservationAvailability.Available) continue;
                // 0x23000003 is AssociatedResumeObject only in an OS-loader object; in the boot
                // manager it means DefaultObject. Do not confuse identical element IDs across roles.
                if (item.ObjectType == WindowsLoaderType && element.Type == 0x23000003 && element.Value.Value is BcdObjectValue resume)
                    Require(resume.ObjectId, BcdRecoveryObjectRole.WindowsResume);
                if (element.Type == 0x14000006 && element.Value.Value is BcdObjectListValue inherited && !inherited.ObjectIds.IsDefault)
                    foreach (var inheritedId in inherited.ObjectIds) Require(inheritedId, BcdRecoveryObjectRole.InheritedSettings);
                if (element.Value.Value is BcdDeviceElementValue device) DeviceOptions(device.Device);
                if (element.QualifiedDevice?.Availability == ObservationAvailability.Available) DeviceOptions(element.QualifiedDevice.Value);
            }
        }
        return issues.Distinct().OrderBy(i => i.ObjectId).ThenBy(i => i.Role).ToImmutableArray();
    }

    private static bool Matches(uint type, BcdRecoveryObjectRole role) => role switch
    {
        BcdRecoveryObjectRole.FirmwareBootManager => type == FirmwareBootManagerType,
        BcdRecoveryObjectRole.WindowsBootManager => type == WindowsBootManagerType,
        BcdRecoveryObjectRole.WindowsLoader => type == WindowsLoaderType,
        BcdRecoveryObjectRole.WindowsResume => type == WindowsResumeType,
        BcdRecoveryObjectRole.DeviceOptions => type == DeviceOptionsType,
        BcdRecoveryObjectRole.InheritedSettings => type >> 28 == 2,
        BcdRecoveryObjectRole.SupportedObject => type >> 28 == 2 || type == DeviceOptionsType ||
            type >> 28 == 1 && (type & 0x0f000000) == 0 && (type >> 20 & 0xf) is >= 1 and <= 4 && (type & 0xfffff) is >= 1 and <= 10,
        _ => false,
    };
}
