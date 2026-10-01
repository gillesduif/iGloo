using System.Text.Json;
using System.Text.Json.Serialization;
using CanonicalImportQualification;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianUserDataPinTests
{
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    [Fact]
    public void ExactHarnessPinSchemaAcceptsRetainedSourceBindingsAndStillRejectsUnknownFields()
    {
        var input = Environment.GetEnvironmentVariable("IGLOO_USERDATA_PIN_REGRESSION");
        var bytes = input is not null ? File.ReadAllBytes(input) : JsonSerializer.SerializeToUtf8Bytes(new
        {
            Use = "DevelopmentImportOnly", ProductionAuthentication = "Unsupported", BuildId = Guid.NewGuid(),
            DescriptorSha256 = new string('A', 64), PolicySha256 = new string('B', 64),
            NotBeforeUtc = DateTimeOffset.Parse("2026-09-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            NotAfterUtc = DateTimeOffset.Parse("2026-10-05T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ManifestSha256 = new string('C', 64), ContentSha256 = new string('D', 64)
        });
        using var document = JsonDocument.Parse(bytes);
        var pin = JsonSerializer.Deserialize<UserDataExternalPin>(bytes, Strict);
        Assert.Equal(document.RootElement.GetProperty("ManifestSha256").GetString(), pin.ManifestSha256);
        Assert.Equal(document.RootElement.GetProperty("ContentSha256").GetString(), pin.ContentSha256);
        var current = pin.NotBeforeUtc.AddMinutes(1);
        var verified = pin.Authenticate(pin.BuildId, pin.DescriptorSha256, pin.ManifestSha256, pin.ContentSha256, current);
        Assert.Equal(pin.PolicySha256, verified.PolicySha256);
        foreach (var changed in new[] { pin with { ManifestSha256 = new string('E', 64) },
            pin with { ContentSha256 = new string('E', 64) }, pin with { PolicySha256 = "" },
            pin with { DescriptorSha256 = "" }, pin with { BuildId = Guid.Empty },
            pin with { NotAfterUtc = current }, pin with { NotBeforeUtc = current.AddDays(1) } })
            Assert.Throws<InvalidDataException>(() => changed.Authenticate(pin.BuildId, pin.DescriptorSha256,
                pin.ManifestSha256, pin.ContentSha256, current));
        foreach (var field in document.RootElement.EnumerateObject().Select(p => p.Name))
        {
            var missing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes)!;
            missing.Remove(field);
            var incomplete = JsonSerializer.Deserialize<UserDataExternalPin>(JsonSerializer.SerializeToUtf8Bytes(missing), Strict);
            Assert.Throws<InvalidDataException>(() => incomplete.Authenticate(pin.BuildId, pin.DescriptorSha256,
                pin.ManifestSha256, pin.ContentSha256, current));
        }
        var extended = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes)!;
        extended.Add("UnexpectedTrustOverride", JsonSerializer.SerializeToElement(true));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserDataExternalPin>(JsonSerializer.SerializeToUtf8Bytes(extended), Strict));
        // Reproduce the executed defect without executing a session or effects.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OldPin>(bytes, Strict));
    }

    private readonly record struct OldPin(string Use, string ProductionAuthentication, Guid BuildId,
        string DescriptorSha256, string PolicySha256, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc);
}
