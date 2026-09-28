using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

// Wire boundary for distros/_shared/installer/collect_inventory.py. Locale-independent JSON;
// no command output text parser or unsuccessful command can supply default identity values.
public static class LinuxInstallerInventoryProtocol
{
    public static Observation<InstallerRuntimeInventoryV1> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (DuplicateProperties(root)) return Fail(ObservationAvailability.Ambiguous, "DuplicateInventoryProperty");
            if (root.GetProperty("schemaVersion").GetInt32() != 1) return Fail(ObservationAvailability.Unsupported, "LinuxInventorySchemaUnsupported");
            var state = State(root.GetProperty("availability").GetString());
            if (state != ObservationAvailability.Available) return Fail(state, root.GetProperty("code").GetString() ?? "LinuxInventoryFailed");
            var disks = root.GetProperty("disks").EnumerateArray().Select(d => new InstallerRuntimeDiskV1(
                Text(d, "devicePath"), d.GetProperty("gptDiskGuid").GetGuid(), d.GetProperty("sizeBytes").GetUInt64(), d.GetProperty("logicalSectorSize").GetUInt32())).ToImmutableArray();
            var partitions = root.GetProperty("partitions").EnumerateArray().Select(p => new InstallerRuntimePartitionV1(
                Text(p, "devicePath"), Text(p, "diskDevicePath"), p.GetProperty("partitionGuid").GetGuid(), p.GetProperty("partitionType").GetGuid(),
                p.GetProperty("offsetBytes").GetUInt64(), p.GetProperty("sizeBytes").GetUInt64(), FileSystem(p.GetProperty("fileSystem")))).ToImmutableArray();
            var inventory = new InstallerRuntimeInventoryV1(disks, partitions)
            {
                ExternalFileSystems = root.GetProperty("externalFileSystems").EnumerateArray()
                    .Select(e => new InstallerExternalFileSystemV1(Text(e, "devicePath"), FileSystem(e.GetProperty("fileSystem")))).ToImmutableArray(),
            };
            var valid = InstallerEspBinding.ValidateInventory(inventory);
            return valid.Availability == ObservationAvailability.Available ? Observations.Available(inventory) : Fail(valid.Availability, valid.Code!);
        }
        catch (JsonException) { return Fail(ObservationAvailability.Ambiguous, "MalformedLinuxInventory"); }
        catch (KeyNotFoundException) { return Fail(ObservationAvailability.Ambiguous, "IncompleteLinuxInventory"); }
        catch (FormatException) { return Fail(ObservationAvailability.Ambiguous, "MalformedLinuxInventoryValue"); }
        catch (InvalidOperationException) { return Fail(ObservationAvailability.Ambiguous, "InvalidLinuxInventoryShape"); }
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new FormatException("Null inventory string.");
    private static Observation<InstallerFileSystemV1> FileSystem(JsonElement element)
    {
        var state = State(Text(element, "availability"));
        return state == ObservationAvailability.Available ? Observations.Available(new InstallerFileSystemV1(Text(element, "type"), element.GetProperty("uuid").GetString())) :
            Observations.Failure<InstallerFileSystemV1>(state, Text(element, "code"));
    }
    private static ObservationAvailability State(string? value) => value switch
    {
        "Available" => ObservationAvailability.Available, "Unavailable" => ObservationAvailability.Unavailable,
        "Unsupported" => ObservationAvailability.Unsupported, "AccessDenied" => ObservationAvailability.AccessDenied,
        "Ambiguous" => ObservationAvailability.Ambiguous, "Absent" => ObservationAvailability.Absent,
        _ => throw new FormatException("Unknown inventory observation state."),
    };
    private static bool DuplicateProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count() ||
            value.EnumerateObject().Any(p => DuplicateProperties(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(DuplicateProperties),
        _ => false,
    };
    private static Observation<InstallerRuntimeInventoryV1> Fail(ObservationAvailability state, string code) => Observations.Failure<InstallerRuntimeInventoryV1>(state, code);
}
