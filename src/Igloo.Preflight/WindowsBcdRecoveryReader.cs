using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

public sealed partial class WindowsBcdReader
{
    // This allowlisted read path neither executes bcdedit text nor invokes any setter/export/import.
    public BcdRecoveryGraphV1 ReadRecoveryGraph()
    {
        try
        {
            using var provider = new ManagementClass(@"\\.\root\WMI:BcdStore");
            using var parameters = provider.GetMethodParameters("OpenStore");
            parameters["File"] = "";
            using var opened = provider.InvokeMethod("OpenStore", parameters, null);
            if (!Succeeded(opened)) return FailedGraph(ObservationAvailability.Unavailable, "BcdOpenStoreFailed");
            using var store = OpenEmbedded((ManagementBaseObject)opened!["Store"]);
            using var enumerate = store.GetMethodParameters("EnumerateObjects");
            enumerate["Type"] = 0u;
            using var result = store.InvokeMethod("EnumerateObjects", enumerate, null);
            if (!Succeeded(result)) return FailedGraph(ObservationAvailability.Unavailable, "BcdObjectEnumerationFailed");
            if (result!["Objects"] is not ManagementBaseObject[] objects) return FailedGraph(ObservationAvailability.Unavailable, "BcdObjectsMissing");
            var rows = ImmutableArray.CreateBuilder<BcdObjectSnapshot>();
            foreach (var item in objects)
                using (item) rows.Add(new(ParseGuid((string)Required(item, "Id")), Convert.ToUInt32(Required(item, "Type"), System.Globalization.CultureInfo.InvariantCulture), ReadElements(item)));
            return new(1, Observations.Available(rows.ToImmutable()), ReadCurrentLoader(store));
        }
        catch (Exception error) when (IsBcdReadError(error))
        { return FailedGraph(WindowsStorageReader.ClassifyError(error), "BcdProviderUnavailable"); }
    }

    private static Observation<Guid> ReadCurrentLoader(ManagementObject store)
    {
        try
        {
            using var input = store.GetMethodParameters("OpenObject");
            // Published current-entry alias GUID; OpenObject accepts GUID syntax, not bcdedit aliases.
            var currentAlias = new Guid("fa926493-6f1c-4193-a414-58f0b2456d1e");
            input["Id"] = currentAlias.ToString("B");
            using var result = store.InvokeMethod("OpenObject", input, null);
            if (!Succeeded(result) || result!["Object"] is not ManagementBaseObject current)
                return Observations.Failure<Guid>(ObservationAvailability.Unavailable, "BcdCurrentLoaderUnavailable");
            using (current)
            {
                var resolved = ParseGuid((string)Required(current, "Id"));
                return resolved == currentAlias ? Observations.Failure<Guid>(ObservationAvailability.Ambiguous, "BcdCurrentAliasUnresolved") : Observations.Available(resolved);
            }
        }
        catch (Exception error) when (IsBcdReadError(error))
        { return Observations.Failure<Guid>(WindowsStorageReader.ClassifyError(error), "BcdCurrentLoaderUnavailable"); }
    }

    private static Observation<ImmutableArray<BcdElementSnapshot>> ReadElements(ManagementBaseObject item)
    {
        try
        {
            using var obj = OpenEmbedded(item);
            using var result = obj.InvokeMethod("EnumerateElements", null, null);
            if (!Succeeded(result) || result!["Elements"] is not ManagementBaseObject[] elements)
                return Observations.Failure<ImmutableArray<BcdElementSnapshot>>(ObservationAvailability.Unavailable, "BcdElementEnumerationFailed");
            var rows = ImmutableArray.CreateBuilder<BcdElementSnapshot>();
            foreach (var element in elements)
                using (element)
                {
                    var type = Convert.ToUInt32(Required(element, "Type"), System.Globalization.CultureInfo.InvariantCulture);
                    Observation<BcdElementValue> value;
                    try { value = Observations.Available(ProjectElement(element)); }
                    catch (Exception error) when (IsBcdReadError(error))
                    { value = Observations.Failure<BcdElementValue>(WindowsStorageReader.ClassifyError(error), "BcdElementInvalid"); }
                    rows.Add(new(type, value, element.ClassPath.ClassName == "BcdDeviceElement" ? ReadQualifiedDevice(obj, type) : null));
                }
            return Observations.Available(rows.ToImmutable());
        }
        catch (Exception error) when (IsBcdReadError(error))
        { return Observations.Failure<ImmutableArray<BcdElementSnapshot>>(WindowsStorageReader.ClassifyError(error), "BcdElementsUnavailable"); }
    }

    private static Observation<BcdDeviceValue> ReadQualifiedDevice(ManagementObject obj, uint type)
    {
        try
        {
            using var input = obj.GetMethodParameters("GetElementWithFlags");
            input["Type"] = type;
            input["Flags"] = 1u;
            using var result = obj.InvokeMethod("GetElementWithFlags", input, null);
            if (!Succeeded(result) || result!["Element"] is not ManagementBaseObject element)
                return Observations.Failure<BcdDeviceValue>(ObservationAvailability.Unavailable, "BcdQualifiedDeviceFailed");
            using (element) return Observations.Available(ProjectDevice((ManagementBaseObject)Required(element, "Device"), 0));
        }
        catch (Exception error) when (IsBcdReadError(error))
        { return Observations.Failure<BcdDeviceValue>(WindowsStorageReader.ClassifyError(error), "BcdQualifiedDeviceUnavailable"); }
    }

    private static BcdElementValue ProjectElement(ManagementBaseObject element) => element.ClassPath.ClassName switch
    {
        "BcdStringElement" => new BcdStringValue((string)Required(element, "String")),
        "BcdIntegerElement" => new BcdIntegerValue(Convert.ToUInt64(Required(element, "Integer"), System.Globalization.CultureInfo.InvariantCulture)),
        "BcdBooleanElement" => new BcdBooleanValue((bool)Required(element, "Boolean")),
        "BcdObjectElement" => new BcdObjectValue(ParseGuid((string)Required(element, "Id"))),
        "BcdObjectListElement" => new BcdObjectListValue(((string[])Required(element, "Ids")).Select(ParseGuid).ToImmutableArray()),
        "BcdIntegerListElement" => new BcdIntegerListValue(((ulong[])Required(element, "Integers")).ToImmutableArray()),
        "BcdDeviceElement" => new BcdDeviceElementValue(ProjectDevice((ManagementBaseObject)Required(element, "Device"), 0)),
        _ => new BcdOpaqueValue(element.ClassPath.ClassName, RawData(element), element.GetText(TextFormat.Mof)),
    };

    private static BcdDeviceValue ProjectDevice(ManagementBaseObject device, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("BCD device nesting exceeds capture limit.");
        var kind = Convert.ToUInt32(Required(device, "DeviceType"), System.Globalization.CultureInfo.InvariantCulture);
        var optionsText = (string)Required(device, "AdditionalOptions");
        Guid? options = string.IsNullOrEmpty(optionsText) ? null : ParseGuid(optionsText);
        return device.ClassPath.ClassName switch
        {
            "BcdDeviceQualifiedPartitionData" when Convert.ToUInt32(Required(device, "PartitionStyle"), System.Globalization.CultureInfo.InvariantCulture) == 1 =>
                new BcdQualifiedGptPartitionDevice(ParseGuid((string)Required(device, "DiskSignature")), ParseGuid((string)Required(device, "PartitionIdentifier")), options),
            "BcdDevicePartitionData" => new BcdPartitionDevice((string)Required(device, "Path"), options),
            "BcdDeviceFileData" => new BcdFileDevice(kind, (string)Required(device, "Path"), ProjectDevice((ManagementBaseObject)Required(device, "Parent"), depth + 1), options),
            _ when kind == 1 => new BcdBootDevice(options),
            _ => new BcdOpaqueDevice(kind, RawData(device), device.ClassPath.ClassName, options, device.GetText(TextFormat.Mof)),
        };
    }

    private static Observation<ImmutableArray<byte>> RawData(ManagementBaseObject value) =>
        value.Properties.Cast<PropertyData>().SingleOrDefault(p => p.Name == "Data")?.Value is byte[] bytes
            ? Observations.Available(bytes.ToImmutableArray())
            : Observations.Failure<ImmutableArray<byte>>(ObservationAvailability.Unsupported, "BcdRawDataNotExposed");
    // Convert.ToUInt*(null) returns zero; a missing provider property is never an observed zero.
    internal static object RequireValue(object? value) => value ?? throw new FormatException("Required BCD provider property missing.");
    private static object Required(ManagementBaseObject value, string name) => RequireValue(value[name]);
    private static ManagementObject OpenEmbedded(ManagementBaseObject obj) => new(@"\\.\root\WMI:" + (string)obj["__RELPATH"]);
    private static bool Succeeded(ManagementBaseObject? result) => result?["ReturnValue"] is true;
    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new FormatException("BCD GUID unavailable.");
    private static bool IsBcdReadError(Exception error) => error is ManagementException or COMException or UnauthorizedAccessException or
        System.Security.SecurityException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException;
    private static BcdRecoveryGraphV1 FailedGraph(ObservationAvailability state, string code) => new(1,
        Observations.Failure<ImmutableArray<BcdObjectSnapshot>>(state, code), Observations.Failure<Guid>(state, code));
}
