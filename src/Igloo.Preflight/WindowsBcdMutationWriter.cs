using System.Collections.Immutable;
using System.Globalization;
using System.Management;
using Igloo.Core.Recovery;

namespace Igloo.Preflight;

internal sealed record BcdMutationCall(Guid ObjectId, string Method, ImmutableDictionary<string, object> Parameters, bool StoreMethod);
internal sealed record BcdMutationResult(bool Succeeded, Guid? CreatedId = null, uint? CreatedType = null);

// Native setters are kept separate from the canonical reader. There is deliberately no CopyObject,
// generated ID, BCDEdit shell, reference cleanup, import/export or alternate-store operation here.
internal sealed class WindowsBcdMutationWriter
{
    private readonly Func<BcdMutationCall, BcdMutationResult> _invoke;
    public WindowsBcdMutationWriter() : this(Invoke) { }
    internal WindowsBcdMutationWriter(Func<BcdMutationCall, BcdMutationResult> invoke) => _invoke = invoke;

    public void Create(Guid id, uint type)
    {
        if (id == Guid.Empty || type != BcdRecoveryRoles.WindowsBootManagerType) throw new InvalidOperationException("Invalid planned BCD creation.");
        var result = _invoke(new(id, "CreateObject", Parameters(("Id", id.ToString("B")), ("Type", type)), true));
        if (!result.Succeeded || result.CreatedId != id || result.CreatedType != type)
            throw new InvalidOperationException("BCD provider did not create the exact planned object. No further mutations may run.");
    }

    public void Delete(Guid id)
    {
        if (id == Guid.Empty || id == BcdRecoveryGraphV1.WindowsBootManagerId || id == BcdRecoveryGraphV1.FirmwareBootManagerId)
            throw new InvalidOperationException("Invalid planned BCD deletion.");
        Require(_invoke(new(id, "DeleteObject", Parameters(("Id", id.ToString("B"))), true)));
    }

    public void Set(Guid id, uint type, BcdElementValue value)
    {
        if (id == Guid.Empty || !BcdMutationPlanning.CanSet(type, value)) throw new NotSupportedException("BCD value has no supported lossless setter.");
        var (method, values) = value switch
        {
            BcdStringValue v => ("SetStringElement", Parameters(("String", v.Text))),
            BcdIntegerValue v => ("SetIntegerElement", Parameters(("Integer", v.Number))),
            BcdBooleanValue v => ("SetBooleanElement", Parameters(("Boolean", v.Boolean))),
            BcdObjectValue v => ("SetObjectElement", Parameters(("Id", v.ObjectId.ToString("B")))),
            BcdObjectListValue v => ("SetObjectListElement", Parameters(("Ids", v.ObjectIds.Select(g => g.ToString("B")).ToArray()))),
            BcdIntegerListValue v => ("SetIntegerListElement", Parameters(("Integers", v.Numbers.ToArray()))),
            BcdDeviceElementValue { Device: BcdQualifiedGptPartitionDevice v } => ("SetQualifiedPartitionDeviceElement",
                Parameters(("PartitionStyle", 1u), ("DiskSignature", v.DiskId.ToString("B")), ("PartitionIdentifier", v.PartitionId.ToString("B")))),
            _ => throw new NotSupportedException("BCD value has no supported lossless setter."),
        };
        Require(_invoke(new(id, method, values.Add("Type", type), false)));
    }

    private static ImmutableDictionary<string, object> Parameters(params (string Name, object Value)[] values) =>
        values.ToImmutableDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
    private static void Require(BcdMutationResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Planned BCD operation failed. No further mutations may run.");
    }

    private static BcdMutationResult Invoke(BcdMutationCall call)
    {
        using var provider = new ManagementClass(@"\\.\root\WMI:BcdStore");
        using var openParameters = provider.GetMethodParameters("OpenStore");
        openParameters["File"] = "";
        using var opened = provider.InvokeMethod("OpenStore", openParameters, null);
        if (opened?["ReturnValue"] is not true || opened["Store"] is not ManagementBaseObject embeddedStore)
            return new(false);
        using (embeddedStore)
        using (var store = new ManagementObject(@"\\.\root\WMI:" + (string)embeddedStore["__RELPATH"]))
        {
            if (call.StoreMethod) return InvokeOn(store, call);
            using var parameters = store.GetMethodParameters("OpenObject");
            parameters["Id"] = call.ObjectId.ToString("B");
            using var result = store.InvokeMethod("OpenObject", parameters, null);
            if (result?["ReturnValue"] is not true || result["Object"] is not ManagementBaseObject embeddedObject) return new(false);
            using (embeddedObject)
            {
                if (!Guid.TryParse(embeddedObject["Id"] as string, out var actual) || actual != call.ObjectId) return new(false);
                using var obj = new ManagementObject(@"\\.\root\WMI:" + (string)embeddedObject["__RELPATH"]);
                return InvokeOn(obj, call);
            }
        }
    }

    private static BcdMutationResult InvokeOn(ManagementObject obj, BcdMutationCall call)
    {
        using var parameters = obj.GetMethodParameters(call.Method);
        foreach (var pair in call.Parameters) parameters[pair.Key] = pair.Value;
        using var result = obj.InvokeMethod(call.Method, parameters, null);
        if (result?["ReturnValue"] is not true) return new(false);
        if (call.Method != "CreateObject") return new(true);
        if (result["Object"] is not ManagementBaseObject created) return new(false);
        using (created)
        {
            if (!Guid.TryParse(created["Id"] as string, out var id) || created["Type"] is null) return new(false);
            return new(true, id, Convert.ToUInt32(created["Type"], CultureInfo.InvariantCulture));
        }
    }
}
