using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Recovery;

public static class RecoverySnapshotSerialization
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static RecoverySnapshotV1 Seal(RecoverySnapshotV1 snapshot)
    { ArgumentNullException.ThrowIfNull(snapshot); return snapshot with { CanonicalHash = ComputeHash(snapshot) }; }
    public static string ComputeHash(RecoverySnapshotV1 snapshot) =>
        Convert.ToHexString(SHA256.HashData(StateBytes(snapshot)));
    public static bool VerifyHash(RecoverySnapshotV1 snapshot)
    { ArgumentNullException.ThrowIfNull(snapshot); return snapshot.CanonicalHash == ComputeHash(snapshot); }

    public static byte[] Serialize(RecoverySnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return CanonicalBytes(Normalize(snapshot));
    }

    // Parse is deliberately not certification. Call Assess after hash verification on every reopen.
    public static RecoverySnapshotV1 Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<RecoverySnapshotV1>(bytes, Options) ?? throw new JsonException("Snapshot missing.");

    internal static byte[] StateBytes(RecoverySnapshotV1 snapshot)
    {
        var s = Normalize(snapshot);
        var closure = BcdDependencyAnalysis.Analyze(s.BootConfiguration, Roots(s));
        var ids = closure.RequiredObjectIds.Concat(s.Scope.BcdMutationObjects).ToHashSet();
        var requiredEntries = RequiredFirmware(s).ToHashSet();
        var graph = s.BootConfiguration.Objects.Availability == ObservationAvailability.Available
            ? Observations.Available(s.BootConfiguration.Objects.Value.Where(o => ids.Contains(o.Id)).Select(o =>
                o.Elements.Availability == ObservationAvailability.Available ? o with
                { Elements = Observations.Available(o.Elements.Value.Select(SemanticElement).ToImmutableArray()) } : o).ToImmutableArray())
            : s.BootConfiguration.Objects;
        return CanonicalBytes(new
        {
            s.SchemaVersion, s.Scope, s.Binding,
            BootConfiguration = s.BootConfiguration with { Objects = graph },
            Firmware = s.Firmware with { BootEntries = s.Firmware.BootEntries.Where(e => requiredEntries.Contains(e.Index)).ToImmutableArray() },
            s.Esp, WinRe = s.WinRe, Rtc = s.Scope.IncludeRtc ? s.Rtc : null,
        }, semantic: true);
    }

    internal static IEnumerable<Guid> Roots(RecoverySnapshotV1 snapshot)
    {
        yield return BcdRecoveryGraphV1.WindowsBootManagerId;
        yield return BcdRecoveryGraphV1.FirmwareBootManagerId;
        if (snapshot.BootConfiguration.CurrentLoaderId.Availability == ObservationAvailability.Available)
            yield return snapshot.BootConfiguration.CurrentLoaderId.Value;
        if (snapshot.WinRe.RecoveryLoaderId.Availability == ObservationAvailability.Available)
            yield return snapshot.WinRe.RecoveryLoaderId.Value;
        // A successfully enumerated absent mutation slot has an exact absent before-state.
        if (snapshot.BootConfiguration.Objects.Availability == ObservationAvailability.Available)
            foreach (var id in snapshot.Scope.BcdMutationObjects)
                if (snapshot.BootConfiguration.Objects.Value.Any(o => o.Id == id)) yield return id;
    }

    internal static IEnumerable<ushort> RequiredFirmware(RecoverySnapshotV1 snapshot) =>
        snapshot.Scope.FirmwareMutationEntries.Append(snapshot.Scope.WindowsBootEntry)
            .Concat(snapshot.Firmware.BootNext.Availability == ObservationAvailability.Available ? [snapshot.Firmware.BootNext.Value] : Array.Empty<ushort>())
            .Distinct().Order();

    private static BcdElementSnapshot SemanticElement(BcdElementSnapshot element) =>
        element.Value.Availability == ObservationAvailability.Available && element.Value.Value is BcdDeviceElementValue device && !BcdDependencyAnalysis.ContainsOpaqueDevice(device.Device) &&
        element.QualifiedDevice?.Availability == ObservationAvailability.Available && BcdDependencyAnalysis.QualifiedDeviceAgrees(device.Device, element.QualifiedDevice.Value)
            ? element with { Value = Observations.Available<BcdElementValue>(new BcdDeviceElementValue(element.QualifiedDevice.Value)), QualifiedDevice = null }
            : element;

    private static RecoverySnapshotV1 Normalize(RecoverySnapshotV1 s) => s with
    {
        Scope = s.Scope with { BcdMutationObjects = s.Scope.BcdMutationObjects.Order().ToImmutableArray(),
            FirmwareMutationEntries = s.Scope.FirmwareMutationEntries.Order().ToImmutableArray() },
        BootConfiguration = s.BootConfiguration with
        {
            Objects = s.BootConfiguration.Objects.Availability == ObservationAvailability.Available
                ? Observations.Available(s.BootConfiguration.Objects.Value.OrderBy(o => o.Id).Select(o =>
                    o.Elements.Availability == ObservationAvailability.Available ? o with
                    { Elements = Observations.Available(o.Elements.Value.OrderBy(e => e.Type).ToImmutableArray()) } : o).ToImmutableArray())
                : s.BootConfiguration.Objects,
        },
        Firmware = s.Firmware with { RequiredBootEntries = s.Firmware.RequiredBootEntries.Order().ToImmutableArray(),
            BootEntries = s.Firmware.BootEntries.OrderBy(e => e.Index).ToImmutableArray() },
        Metadata = s.Metadata with { VolumeLocators = s.Metadata.VolumeLocators.OrderBy(v => v.VolumeGuid).ToImmutableArray(),
            DiagnosticCodes = s.Metadata.DiagnosticCodes.Order(StringComparer.Ordinal).ToImmutableArray() },
    };

    internal static bool ValueEqual<T>(T first, T second) => CanonicalBytes(first).AsSpan().SequenceEqual(CanonicalBytes(second));

    private static byte[] CanonicalBytes<T>(T value, bool semantic = false)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteCanonical(writer, element, semantic);
        return output.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool semantic)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Polymorphic discriminator must precede other members for System.Text.Json on .NET 8.
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name == "kind" ? 0 : 1).ThenBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (semantic && property.Name is "code" or "NativeError" or "ObservedPartitionNumber" or "ProviderEvidence") continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, semantic);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item, semantic);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { MaxDepth = 128, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new BytesConverter());
        options.Converters.Add(new ObservationConverterFactory());
        return options;
    }

    private sealed class BytesConverter : JsonConverter<ImmutableArray<byte>>
    {
        public override ImmutableArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetBytesFromBase64().ToImmutableArray();
        public override void Write(Utf8JsonWriter writer, ImmutableArray<byte> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) throw new JsonException("Default bytes are not observed empty bytes.");
            writer.WriteBase64StringValue(value.AsSpan());
        }
    }

    private sealed class ObservationConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Observation<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ObservationConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Closed generic converters are instantiated by ObservationConverterFactory through reflection.")]
    private sealed class ObservationConverter<T> : JsonConverter<Observation<T>>
    {
        public override Observation<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!Enum.TryParse<ObservationAvailability>(root.GetProperty("availability").GetString(), out var state) || !Enum.IsDefined(state))
                throw new JsonException("Unknown observation state.");
            if (root.EnumerateObject().Any(p => p.Name is not ("availability" or "code" or "value"))) throw new JsonException("Unknown observation field.");
            if (state == ObservationAvailability.Available)
            {
                var value = root.GetProperty("value").Deserialize<T>(options);
                return value is null ? throw new JsonException("Available observation has no value.") : Observations.Available(value);
            }
            if (root.TryGetProperty("value", out _)) throw new JsonException("Failed observation contains a value.");
            return Observations.Failure<T>(state, root.GetProperty("code").GetString() ?? throw new JsonException("Failure code missing."));
        }

        public override void Write(Utf8JsonWriter writer, Observation<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("availability", value.Availability.ToString());
            if (value.Availability == ObservationAvailability.Available)
            { writer.WritePropertyName("value"); JsonSerializer.Serialize(writer, value.Value, options); }
            else writer.WriteString("code", value.Code);
            writer.WriteEndObject();
        }
    }
}
