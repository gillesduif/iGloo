using System.Security;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;

namespace Igloo.Core.Runtime;

public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly Dictionary<(string Id, int Version), ICommandHandler> _handlers;

    public CommandDispatcher(IPreflightChecker checker, DistroLoader loader, DistroRegistry registry,
        IEnumerable<ICommandHandler>? additionalHandlers = null,
        IAdvisoryPlanJournal? planJournal = null)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(registry);
        ICommandHandler[] handlers =
        [
            new CapabilitiesHandler(() => Capabilities),
            new CapabilityDescribeHandler(() => Capabilities),
            new VersionHandler(),
            new SystemInspectHandler(checker),
            new SystemInspectDetailHandler(checker),
            new DistroListHandler(loader),
            new DistroInspectHandler(loader),
            new DistroCompatibilityHandler(checker, registry, new DistroCompatibilityEvaluator(registry, loader)),
            new MigrationInspectHandler(checker),
            new MigrationPlanHandler(checker, loader, registry, new DistroCompatibilityEvaluator(registry, loader)),
            new RecoveryInspectHandler(),
        ];
        _handlers = handlers.Concat(planJournal is null ? [] :
                [new MigrationPlanHandler(checker, loader, registry,
                    new DistroCompatibilityEvaluator(registry, loader), 2, planJournal),
                    new MigrationPlanReadHandler(planJournal)])
            .Concat(additionalHandlers ?? []).ToDictionary(h => (h.Descriptor.Id,
                h.Descriptor.Version));
    }

    public IReadOnlyList<CapabilityDescriptor> Capabilities =>
        _handlers.Values.Select(h => h.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Version).ToArray();

    public async Task<CommandResult> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var error = Validate(request, out var handler);
        if (error is not null)
            return Failure(request, error);
        // Validation and pre-dispatch cancellation do not allocate an operation.
        // Once a mutation gets one, every terminal outcome keeps that identity.
        var effective = request;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = handler!.Descriptor.Kind == CommandKind.Mutation && request.OperationId is null
                ? request with { OperationId = Guid.NewGuid() } : request;
            // A journaled mutation must persist its operation identity before it
            // can appear in progress or a terminal result. Failed admission leaves
            // effective at the original request, with no newly issued identity.
            if (handler is IDurableCommandAdmission admission)
            {
                // Even a caller-nominated ID is not evidence that admission
                // persisted; hide it in any pre-admission failure result.
                effective = request with { OperationId = null };
                await admission.AdmitAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            effective = candidate;
            progress?.Report(new(effective.RequestId, effective.OperationId, "validating"));
            var data = await handler.ExecuteAsync(effective, progress, cancellationToken).ConfigureAwait(false);
            var observed = data as ObservedPayload;
            if (observed is not null) data = observed.Data;
            return new(CommandProtocol.Version, request.CapabilityId, handler.Descriptor.Version,
                request.RequestId, request.CorrelationId, effective.OperationId, true,
                JsonSerializer.SerializeToElement(data, CommandProtocol.JsonOptions), null, [], [],
                observed?.Fingerprint);
        }
        catch (OperationCanceledException)
        {
            return Failure(effective, new(CommandErrorCode.Cancelled, "Operation cancelled."));
        }
        catch (JsonException)
        {
            return Failure(effective, new(CommandErrorCode.InvalidRequest, "Input does not match the capability schema."));
        }
        catch (ArgumentException)
        {
            return Failure(effective, new(CommandErrorCode.InvalidRequest, "Input does not match the capability schema."));
        }
        catch (KeyNotFoundException)
        {
            return Failure(effective, new(CommandErrorCode.NotSupported, "The requested item is unavailable."));
        }
        catch (ResourceBusyException)
        {
            return Failure(effective, new(CommandErrorCode.ResourceBusy, "A conflicting local operation is running."));
        }
        catch (IsoAcquisitionFailureException ex)
        {
            // Only fixed codes and copy cross the command boundary. The local
            // exception may contain mirror URLs, cache paths or HTTP details.
            var isoError = ex.Kind switch
            {
                IsoAcquisitionFailureKind.NetworkUnavailable => new CommandError(
                    CommandErrorCode.InternalFailure,
                    "The distribution image or verification data could not be fetched. Check the connection and retry.",
                    "IsoNetworkUnavailable"),
                IsoAcquisitionFailureKind.CacheUnavailable => new CommandError(
                    CommandErrorCode.InternalFailure,
                    "The local image cache could not be used. Check available storage and retry.",
                    "IsoCacheUnavailable"),
                IsoAcquisitionFailureKind.VerificationFailed => new CommandError(
                    CommandErrorCode.VerificationFailed,
                    "The installer image or signed metadata failed verification.",
                    "IsoVerificationFailed"),
                IsoAcquisitionFailureKind.SourceMetadataUnavailable => new CommandError(
                    CommandErrorCode.VerificationFailed,
                    "Trusted installer image information is unavailable for this distribution.",
                    "IsoSourceMetadataUnavailable"),
                _ => new CommandError(CommandErrorCode.InternalFailure,
                    "The local operation failed. Consult protected local diagnostics."),
            };
            return Failure(effective, isoError);
        }
        catch (ArtifactVerificationException)
        {
            return Failure(effective, new(CommandErrorCode.VerificationFailed,
                "Artifact verification failed."));
        }
        catch (PlanJournalCorruptException)
        {
            return Failure(effective, new(CommandErrorCode.VerificationFailed,
                "Advisory plan record failed integrity checks."));
        }
        catch (CommandEvidenceException)
        {
            return Failure(effective, new(CommandErrorCode.VerificationFailed,
                "Local evidence failed integrity checks."));
        }
        catch (StateChangedException)
        {
            return Failure(effective, new(CommandErrorCode.StateChanged,
                "Observed endpoint state changed; inspect and plan again."));
        }
        catch (CommandPreconditionException)
        {
            return Failure(effective, new(CommandErrorCode.PreconditionFailed,
                "Required endpoint preconditions are unavailable."));
        }
        catch (OperationJournalUnavailableException)
        {
            return Failure(effective, new(CommandErrorCode.RecoveryRequired,
                "Inspect operation status before retrying; iGloo could not confirm the local operation state."));
        }
        catch (NotSupportedException)
        {
            return Failure(effective, new(CommandErrorCode.NotSupported, "This operation is not supported for the selected provider."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException)
        {
            return Failure(effective, new(CommandErrorCode.InternalFailure, "The local operation failed. Consult protected local diagnostics."));
        }
        // The handler may fail with an unclassified exception after an operation
        // was assigned and reported. Keep that identity in a sanitized terminal
        // result; the outer CLI boundary only has the original request.
#pragma warning disable CA1031 // The command boundary must convert unknown handler failures to a bound result.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Failure(effective, new(CommandErrorCode.InternalFailure,
                "The local operation failed. Consult protected local diagnostics."));
        }
    }

    private CommandError? Validate(CommandRequest request, out ICommandHandler? handler)
    {
        handler = null;
        if (request.ProtocolVersion != CommandProtocol.Version)
            return new(CommandErrorCode.UnsupportedVersion, "Unsupported command protocol version.");
        if (request.RequestId == Guid.Empty || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.CapabilityId))
            return new(CommandErrorCode.InvalidRequest, "Request ID, correlation ID and capability ID are required.");
        if (!_handlers.TryGetValue((request.CapabilityId, request.CapabilityVersion), out handler))
            return _handlers.Keys.Any(k => k.Id == request.CapabilityId)
                ? new(CommandErrorCode.UnsupportedVersion, "Unsupported capability version.")
                : new(CommandErrorCode.UnsupportedCapability, "Capability is unavailable.");
        if (request.DryRun && !handler.Descriptor.SupportsDryRun)
            return new(CommandErrorCode.InvalidRequest, "This capability has no dry-run mode.");
        if (request.Input.ValueKind != JsonValueKind.Object)
            return new(CommandErrorCode.InvalidRequest, "Input must be a JSON object.");
        if ((handler.Descriptor.Id is "system.version" or "system.capabilities" or "system.inspect" or "system.inspect.detail" or "distro.list" or "migration.inspect" or "recovery.inspect")
            && request.Input.ValueKind == JsonValueKind.Object && request.Input.EnumerateObject().Any())
            return new(CommandErrorCode.InvalidRequest, "This capability takes no input fields.");
        return null;
    }

    private static CommandResult Failure(CommandRequest request, CommandError error) =>
        new(CommandProtocol.Version, request.CapabilityId, request.CapabilityVersion,
            request.RequestId, request.CorrelationId, request.OperationId, false, null, error, [], []);
}

public sealed class ResourceBusyException : Exception
{
    public ResourceBusyException() : base("Resource busy.") { }
    public ResourceBusyException(string message) : base(message) { }
    public ResourceBusyException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ArtifactVerificationException : Exception
{
    public ArtifactVerificationException() : base("Artifact verification failed.") { }
    public ArtifactVerificationException(string message) : base(message) { }
    public ArtifactVerificationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class CommandEvidenceException : Exception
{
    public CommandEvidenceException() : base("Local evidence failed integrity checks.") { }
    public CommandEvidenceException(string message) : base(message) { }
    public CommandEvidenceException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class StateChangedException : Exception
{
    public StateChangedException() : base("Observed state changed.") { }
    public StateChangedException(string message) : base(message) { }
    public StateChangedException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class CommandPreconditionException : Exception
{
    public CommandPreconditionException() : base("Required preconditions are unavailable.") { }
    public CommandPreconditionException(string message) : base(message) { }
    public CommandPreconditionException(string message, Exception innerException) : base(message, innerException) { }
}

public interface ICommandHandler
{
    CapabilityDescriptor Descriptor { get; }
    Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Persist a mutation identity before the dispatcher reports it to a client.</summary>
public interface IDurableCommandAdmission
{
    Task AdmitAsync(CommandRequest request, CancellationToken cancellationToken);
}

public sealed class OperationJournalUnavailableException : Exception
{
    public OperationJournalUnavailableException() : base("Operation journal unavailable.") { }
    public OperationJournalUnavailableException(string message) : base(message) { }
    public OperationJournalUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class CapabilitiesHandler(Func<IReadOnlyList<CapabilityDescriptor>> catalogue) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("system.capabilities", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User, []);
    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult<object>(catalogue());
}

internal sealed class CapabilityDescribeHandler(Func<IReadOnlyList<CapabilityDescriptor>> catalogue) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("system.capability.describe", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User, []);
    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selection = request.Input.Deserialize<CapabilitySelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(selection?.CapabilityId)) throw new ArgumentException("Missing capability ID.");
        return Task.FromResult<object>(catalogue().Where(c => c.Id == selection.CapabilityId)
            .OrderByDescending(c => c.Version).FirstOrDefault()
            ?? throw new KeyNotFoundException());
    }
}

internal sealed class VersionHandler : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("system.version", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User, []);
    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult<object>(new { ProtocolVersion = CommandProtocol.Version,
            RuntimeVersion = typeof(CommandDispatcher).Assembly.GetName().Version?.ToString() });
}

internal sealed class SystemInspectHandler(IPreflightChecker checker) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("system.inspect", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);
    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        return new ObservedPayload(PublicInspectionMapper.MapSummary(report),
            ObservedStateRevision.Fingerprint(report));
    }
}

internal sealed class SystemInspectDetailHandler(IPreflightChecker checker) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("system.inspect.detail", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        return new ObservedPayload(PublicInspectionMapper.MapDetail(report),
            ObservedStateRevision.Fingerprint(report));
    }
}

internal sealed class DistroListHandler(DistroLoader loader) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("distro.list", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User, []);
    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult<object>(new DistroList(loader.LoadedDistros
            .Select(d => new DistroSummary(d.Id, d.DisplayName, d.Status))
            .OrderBy(d => d.Id, StringComparer.Ordinal).ToArray()));
}

internal sealed class DistroInspectHandler(DistroLoader loader) : ICommandHandler
{
    public CapabilityDescriptor Descriptor => new("distro.inspect", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User,
        loader.LoadedDistros.Select(d => d.Id).Order(StringComparer.Ordinal).ToArray());
    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selection = DistroInput.Read(request.Input);
        var distro = loader.LoadedDistros.SingleOrDefault(d => string.Equals(d.Id,
            selection.DistroId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException();
        return Task.FromResult<object>(new DistroSummary(distro.Id, distro.DisplayName, distro.Status));
    }
}

internal sealed class DistroCompatibilityHandler(IPreflightChecker checker, DistroRegistry registry,
    DistroCompatibilityEvaluator evaluator) : ICommandHandler
{
    public CapabilityDescriptor Descriptor => new("distro.compatibility", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User,
        registry.Plugins.Keys.Order(StringComparer.Ordinal).ToArray());
    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selection = DistroInput.Read(request.Input);
        if (!registry.TryGet(selection.DistroId, out var plugin)) throw new KeyNotFoundException();
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        var findings = evaluator.Evaluate(plugin.Id, report);
        return new ObservedPayload(new DistroCompatibility(plugin.Id,
            findings.Select(PublicInspectionMapper.MapFinding).ToArray(),
            findings.Any(f => f.Severity == FindingSeverity.Blocker)),
            ObservedStateRevision.Fingerprint(report));
    }
}

internal static class DistroInput
{
    public static DistroSelection Read(JsonElement input)
    {
        var value = input.Deserialize<DistroSelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(value?.DistroId)) throw new ArgumentException("Missing distro ID.");
        return value;
    }
}
