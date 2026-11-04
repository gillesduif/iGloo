using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Runtime;

namespace Igloo.Core.Runtime.Execution;

public enum LocalOperationState
{
    Accepted, Validating, Preparing, Running, AwaitingReboot, Continuing,
    Verifying, Completed, Failed, RecoveryRequired, Cancelled,
}

/// <summary>Only safe continuation metadata. Never write a manifest, path, credential or raw exception.</summary>
public sealed record LocalOperationRecord(Guid OperationId, string CapabilityId,
    Guid RequestId, Guid CorrelationId, LocalOperationState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string TargetIdentityHash,
    string CurrentStage, IReadOnlyList<string> EvidenceCodes,
    string? FailureCode = null, string? ContinuationCode = null);

public interface IOperationJournal
{
    Task<LocalOperationRecord?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalOperationRecord>> FindAsync(string capabilityId,
        string targetIdentityHash, CancellationToken cancellationToken = default);
    Task CreateAsync(CommandRequest request, string targetIdentityHash,
        CancellationToken cancellationToken = default);
    Task TransitionAsync(Guid operationId, LocalOperationState state, string stage,
        IReadOnlyList<string>? evidenceCodes = null, string? failureCode = null,
        string? continuationCode = null, CancellationToken cancellationToken = default);
}

public sealed class FileOperationJournal(string directory) : IOperationJournal
{
    public static string DefaultDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "operations");

    public async Task<LocalOperationRecord?> ReadAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID is required.");
        var path = Path.Join(directory, operationId.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<LocalOperationRecord>(stream,
            CommandProtocol.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Invalid operation journal record.");
    }

    public async Task<IReadOnlyList<LocalOperationRecord>> FindAsync(string capabilityId,
        string targetIdentityHash, CancellationToken cancellationToken = default)
    {
        ValidateCode(capabilityId);
        ValidateCode(targetIdentityHash);
        if (!Directory.Exists(directory)) return [];
        var matching = new List<LocalOperationRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                throw new InvalidDataException("Invalid operation journal filename.");
            var record = await ReadAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Operation journal record disappeared.");
            if (record.OperationId != id || record.RequestId == Guid.Empty ||
                record.CorrelationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(record.CapabilityId) ||
                string.IsNullOrWhiteSpace(record.TargetIdentityHash) ||
                record.EvidenceCodes is null)
                throw new InvalidDataException("Operation journal record is incomplete.");
            if (string.Equals(record.CapabilityId, capabilityId, StringComparison.Ordinal) &&
                string.Equals(record.TargetIdentityHash, targetIdentityHash,
                    StringComparison.Ordinal))
                matching.Add(record);
        }
        return matching.OrderByDescending(record => record.CreatedAt)
            .ThenBy(record => record.OperationId).ToArray();
    }

    public async Task CreateAsync(CommandRequest request, string targetIdentityHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId is not { } operationId || operationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.");
        ValidateCode(targetIdentityHash);
        Directory.CreateDirectory(directory);
        using var lease = await ResourceLock.AcquireAsync("journal:" + operationId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
        if (await ReadAsync(operationId, cancellationToken).ConfigureAwait(false) is not null)
            throw new ResourceBusyException("Operation identity already exists.");
        var now = DateTimeOffset.UtcNow;
        var record = new LocalOperationRecord(operationId, request.CapabilityId,
            request.RequestId, request.CorrelationId, LocalOperationState.Accepted,
            now, now, targetIdentityHash, "accepted", []);
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task TransitionAsync(Guid operationId, LocalOperationState state, string stage,
        IReadOnlyList<string>? evidenceCodes = null, string? failureCode = null,
        string? continuationCode = null, CancellationToken cancellationToken = default)
    {
        ValidateCode(stage);
        foreach (var code in evidenceCodes ?? []) ValidateCode(code);
        if (failureCode is not null) ValidateCode(failureCode);
        if (continuationCode is not null) ValidateCode(continuationCode);
        using var lease = await ResourceLock.AcquireAsync("journal:" + operationId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
        var prior = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Operation is unavailable.");
        if (prior.State is LocalOperationState.Completed or LocalOperationState.Failed
            or LocalOperationState.Cancelled or LocalOperationState.RecoveryRequired)
            throw new InvalidOperationException("Operation has a terminal state.");
        var next = prior with { State = state, UpdatedAt = DateTimeOffset.UtcNow,
            CurrentStage = stage, EvidenceCodes = evidenceCodes ?? prior.EvidenceCodes,
            FailureCode = failureCode, ContinuationCode = continuationCode };
        await SaveAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(LocalOperationRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, record.OperationId.ToString("N") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, record, CommandProtocol.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (':' or '-' or '_' or '.')))
            throw new ArgumentException("Journal metadata must be a short safe code.");
    }
}

public sealed class OperationStatusHandler(IOperationJournal journal, bool migrationOnly) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new(migrationOnly ? "migration.status" : "operation.status",
        1, 1, 1, CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<OperationSelection>(CommandProtocol.JsonOptions);
        if (selection is null || selection.OperationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.");
        var record = await journal.ReadAsync(selection.OperationId, cancellationToken).ConfigureAwait(false);
        if (record is null || (migrationOnly && !record.CapabilityId.StartsWith("migration.",
                StringComparison.Ordinal))) throw new KeyNotFoundException();
        return new OperationStatus(record.OperationId, record.CapabilityId, record.State.ToString(),
            record.CreatedAt, record.UpdatedAt, record.CurrentStage, record.EvidenceCodes,
            record.FailureCode, record.ContinuationCode);
    }
}
