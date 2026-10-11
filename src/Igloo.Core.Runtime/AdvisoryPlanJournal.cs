using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Command.Contracts;

namespace Igloo.Core.Runtime;

/// <summary>Advisory plan metadata only. A saved plan never grants execution authority.</summary>
public interface IAdvisoryPlanJournal
{
    Task SaveAsync(MigrationPlanResultV2 plan, CommandRequest request,
        CancellationToken cancellationToken = default);
    Task<MigrationPlanResultV2?> ReadAsync(Guid planId, CancellationToken cancellationToken = default);
}

public sealed class FileAdvisoryPlanJournal(string directory) : IAdvisoryPlanJournal
{
    public static string DefaultDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "plans");

    public async Task SaveAsync(MigrationPlanResultV2 plan, CommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidPlan(plan))
            throw new ArgumentException("Invalid advisory plan.");
        Directory.CreateDirectory(directory);
        var record = new PlanRecord(1, plan, request.RequestId, request.CorrelationId,
            DateTimeOffset.UtcNow, null);
        record = record with { IntegritySha256 = Integrity(record) };
        var destination = Path.Join(directory, plan.PlanId.ToString("N") + ".json");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, record, CommandProtocol.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination); // No overwrite, even after an ID collision.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<MigrationPlanResultV2?> ReadAsync(Guid planId,
        CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty) throw new ArgumentException("Plan ID is required.");
        var path = Path.Join(directory, planId.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        PlanRecord? record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<PlanRecord>(stream,
                CommandProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new PlanJournalCorruptException();
        }
        if (record is null || record.SchemaVersion != 1 || record.Plan is null ||
            record.Plan.PlanId != planId || !ValidPlan(record.Plan) ||
            !string.Equals(record.IntegritySha256,
                Integrity(record with { IntegritySha256 = null }), StringComparison.Ordinal))
            throw new PlanJournalCorruptException();
        return record.Plan;
    }

    private static bool ValidPlan(MigrationPlanResultV2 plan) =>
        plan.PlanId != Guid.Empty && !string.IsNullOrWhiteSpace(plan.DistroId) &&
        plan.RequiredBytes >= 0 && plan.ObservedAvailableBytes is not < 0 &&
        !plan.Executable && plan.Disposition == "AdvisoryOnly" &&
        plan.BlockerCodes is { Count: <= 64 } &&
        IsDigest(plan.ObservedStateFingerprint) &&
        IsDigest(plan.ObservedMachineFingerprint) &&
        IsDigest(plan.ProviderRevisionFingerprint);

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Integrity(PlanRecord record) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(record, CommandProtocol.JsonOptions)));

    private sealed record PlanRecord(int SchemaVersion, MigrationPlanResultV2 Plan,
        Guid RequestId, Guid CorrelationId, DateTimeOffset CreatedAt,
        string? IntegritySha256);
}

public sealed class PlanJournalCorruptException : Exception
{
    public PlanJournalCorruptException() : base("Advisory plan record is corrupt.") { }
    public PlanJournalCorruptException(string message) : base(message) { }
    public PlanJournalCorruptException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class MigrationPlanReadHandler(IAdvisoryPlanJournal journal) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("migration.plan.read", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<PlanSelection>(CommandProtocol.JsonOptions);
        if (selection is null || selection.PlanId == Guid.Empty)
            throw new ArgumentException("Plan ID is required.");
        return await journal.ReadAsync(selection.PlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException();
    }
}
