using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Igloo.Fleet.Domain;

/// <summary>Schema 1: ordinal object keys, original array order, System.Text.Json primitive representation.</summary>
public static class EvidenceIntegrity
{
    public static string Hash<T>(T payload)
    {
        return HashBytes(CanonicalBytes(payload));
    }

    public static byte[] CanonicalBytes<T>(T payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, document.RootElement);
        return stream.ToArray();
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string SecretHash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                Write(writer, item);
            writer.WriteEndArray();
        }
        else
            element.WriteTo(writer);
    }
}
