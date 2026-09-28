using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianRootTransportTests
{
    [Fact]
    public void NativeProducerEnvelopeReopensWithoutChangingItsBytes()
    {
        using var stream = typeof(DebianRootTransportTests).Assembly.GetManifestResourceStream("DebianRootTransportQualification.json")!;
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        var plan = Plan(4_641_457_180) with
        {
            BuildId = Guid.Parse("99695826-82bc-4077-b4cf-a22cebc706da"), DerivationId = Guid.Parse("6072529e-1484-4fc7-a68c-d05c970bb393"),
            DescriptorSha256 = "4936F2FBC669253A93CB9004D3EF4E268B995AC30479CBEF1C1E83F5B8234A20",
            ManifestSha256 = "5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2",
            ContentSha256 = "EDE9ADCC24597B82389CE7C73D38A02A3AB077DA53C132EE9704FEC71BC705F5",
            Transport = "Chunked", TransportManifestSha256 = "A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757",
        };
        Assert.Equal(5, DebianRootTransports.Reopen(bytes, plan).Chunks.Length);
        // Codec evidence only: this fixture ownership is never used to execute the real artifact.
        Assert.Throws<InvalidDataException>(() => DebianRootTransports.Reopen(bytes, plan with { BuildId = Guid.NewGuid() }));
    }

    private static DebianConfiguredRootImportPlanV1 Plan(long length)
    {
        var storage = TargetRootFixture.Create();
        return new(storage.Ownership, storage.Root, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"), new('A', 64), new('D', 64), new('B', 64), new('C', 64), length);
    }

    private static JsonObject Envelope(DebianConfiguredRootImportPlanV1 plan) => JsonSerializer.SerializeToNode(new
    {
        Version = 1, Type = "Chunked", plan.BuildId, plan.DerivationId, plan.DescriptorSha256, plan.ManifestSha256,
        plan.ContentLength, plan.ContentSha256, ChunkSize = DebianRootTransports.ChunkSize,
        Chunks = Enumerable.Range(0, checked((int)((plan.ContentLength + DebianRootTransports.ChunkSize - 1) / DebianRootTransports.ChunkSize)))
            .Select(i => new { Index = i, Name = FormattableString.Invariant($"root.content.{i:D4}"),
                Length = Math.Min(DebianRootTransports.ChunkSize, plan.ContentLength - i * DebianRootTransports.ChunkSize), Sha256 = new string('E', 64) }),
    })!.AsObject();

    private static DebianRootTransportV1 Reopen(JsonObject value, DebianConfiguredRootImportPlanV1 plan)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        return DebianRootTransports.Reopen(bytes, plan with { Transport = "Chunked", TransportManifestSha256 = DebianConfiguredRootArtifacts.Digest(bytes) });
    }

    [Fact]
    public void OriginalLargeArtifactHasFiveFixedPhysicalChunks()
    {
        var plan = Plan(4_641_457_180);
        Assert.Equal(ObservationAvailability.Unsupported, plan.TransportSupport.Availability);
        var decoded = Reopen(Envelope(plan), plan);
        Assert.Equal(new long[] { 1073741824, 1073741824, 1073741824, 1073741824, 346489884 }, decoded.Chunks.Select(c => c.Length));
        Assert.Equal(plan.ContentLength, decoded.Chunks.Sum(c => c.Length));
    }

    [Fact]
    public void ExplicitSelectionAndTransportIdentityArePlanBound()
    {
        var plan = Plan(3);
        Assert.Equal(ObservationAvailability.Available, plan.TransportSupport.Availability);
        var chunked = plan with { Transport = "Chunked", TransportManifestSha256 = new('F', 64) };
        Assert.Equal(ObservationAvailability.Available, chunked.TransportSupport.Availability);
        Assert.NotEqual(plan.Fingerprint(), chunked.Fingerprint());
        Assert.NotEqual(chunked.Fingerprint(), (chunked with { OtherPayloadBytes = 1 }).Fingerprint());
        Assert.Throws<InvalidDataException>(() => (plan with { TransportManifestSha256 = new('F', 64) }).Fingerprint());
        Assert.Throws<InvalidDataException>(() => (plan with { Transport = "Automatic" }).Fingerprint());
        Assert.Throws<InvalidDataException>(() => (plan with { Transport = "Chunked" }).Fingerprint());
    }

    [Theory]
    [InlineData("Index", "1")]
    [InlineData("Index", "true")]
    [InlineData("Length", "0")]
    [InlineData("Length", "3.0")]
    [InlineData("Length", "3e0")]
    [InlineData("Length", "9223372036854775808")]
    [InlineData("Name", "\"../root.content\"")]
    [InlineData("Name", "\"/root.content\"")]
    [InlineData("Name", "\"ROOT.CONTENT.0000\"")]
    [InlineData("Sha256", "\"bad\"")]
    [InlineData("Unknown", "1")]
    public void InvalidPhysicalObjectIsRejected(string field, string json)
    {
        var plan = Plan(3); var value = Envelope(plan);
        value["Chunks"]![0]![field] = JsonNode.Parse(json);
        Assert.ThrowsAny<Exception>(() => Reopen(value, plan));
    }

    [Fact]
    public void DuplicateFieldsAndWrongBindingsAreRejected()
    {
        var plan = Plan(3); var value = Envelope(plan);
        var bytes = Encoding.UTF8.GetBytes("{\"Version\":1," + value.ToJsonString()[1..]);
        Assert.Throws<InvalidDataException>(() => DebianRootTransports.Reopen(bytes,
            plan with { Transport = "Chunked", TransportManifestSha256 = DebianConfiguredRootArtifacts.Digest(bytes) }));
        value["ContentSha256"] = new string('F', 64);
        Assert.Throws<InvalidDataException>(() => Reopen(value, plan));
    }

    [Fact]
    public void SwappedMissingOrDuplicateIndicesAreRejected()
    {
        var plan = Plan(DebianRootTransports.ChunkSize + 1);
        var value = Envelope(plan); value["Chunks"]![1]!["Index"] = 0;
        Assert.Throws<InvalidDataException>(() => Reopen(value, plan));
        value = Envelope(plan); value["Chunks"]!.AsArray().RemoveAt(1);
        Assert.Throws<InvalidDataException>(() => Reopen(value, plan));
        value = Envelope(plan); value["Chunks"]![1]!["Name"] = "root.content.0000";
        Assert.Throws<InvalidDataException>(() => Reopen(value, plan));
    }
}
