using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

// Fixture-only wire bridge. It emits the actual profile bytes and consumes a
// fresh native observer result, but creates no installation authority.
internal static class LabOfflineAgent
{
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static int Export(string generation, string operation, string targetRoot, bool canonical = false)
    {
        var stage = canonical
            ? DebianAgentProfiles.DeclareLabOfflineStage(Guid.Parse(generation), Guid.Parse(operation), Guid.Parse(targetRoot))
            : DebianAgentProfiles.DeclareOfflineStage(Guid.Parse(generation), Guid.Parse(operation), Guid.Parse(targetRoot));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Declaration = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(stage)),
            Worker = Convert.ToBase64String(DebianFirstBootEvidence.WorkerBytes()),
            Unit = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebianAgentProfiles.Unit))
        }));
        return 0;
    }

    internal static async Task<int> VerifyAsync()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var bounded = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk)) != 0)
            {
                if (bounded.Length + count > 1024 * 1024) throw new InvalidDataException("AgentReadbackOversized");
                await bounded.WriteAsync(chunk.AsMemory(0, count));
            }
            using var document = JsonDocument.Parse(bounded.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "Declaration", "Readback" }.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("AgentReadbackShape");
            var stage = JsonSerializer.Deserialize<DebianAgentOfflineStageV1>(root.GetProperty("Declaration").GetBytesFromBase64(), Strict)!;
            var actual = JsonSerializer.Deserialize<DebianAgentOfflineReadbackV1>(root.GetProperty("Readback").GetBytesFromBase64(), Strict)!;
            var result = DebianAgentProfiles.VerifyOfflineStage(stage, Observations.Available(actual));
            if (result.Availability != ObservationAvailability.Available) throw new InvalidDataException(result.Code);
            Console.WriteLine(JsonSerializer.Serialize(new { State = "StagedInputsPending", stage.GenerationId,
                stage.OperationId, stage.TargetRootId, ConfigurationInstalled = false, EnableLinkInstalled = false,
                ServiceStarted = false, CompletionReceiptProduced = false, EffectAuthorization = false }));
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { State = "Rejected", Code = "OfflineAgentStageReadbackRejected",
                CompletionReceiptProduced = false, EffectAuthorization = false }));
            return 2;
        }
    }
}
