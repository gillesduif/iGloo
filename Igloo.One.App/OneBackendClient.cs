using Igloo.Cli.Client;
using Igloo.Command.Contracts;

namespace Igloo.One.App;

/// <summary>CLI command binding for the unfinished One shell; no AI or privileged service access.</summary>
internal sealed class OneBackendClient(CliCommandClient commands) : IAsyncDisposable
{
    public Task<IReadOnlyList<CapabilityDescriptor>> ListCapabilitiesAsync(
        CancellationToken cancellationToken = default) => commands.DiscoverAsync(cancellationToken);

    public static OneBackendClient Create() => new(
        CliCommandClient.ForApplication(AppContext.BaseDirectory, TimeSpan.FromMinutes(2)));

    public Task<CommandResult> InspectSystemAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync("system.inspect", new { }, cancellationToken);

    public Task<CommandResult> ListDistrosAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync("distro.list", new { }, cancellationToken);

    public Task<CommandResult> InspectCompatibilityAsync(string distroId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("distro.compatibility", new DistroSelection(distroId), cancellationToken);

    public Task<CommandResult> PlanMigrationAsync(MigrationPlanInput input,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("migration.plan", input, cancellationToken);

    public Task<CommandResult> InspectOperationAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync("operation.status", new OperationSelection(operationId), cancellationToken);

    private Task<CommandResult> InvokeAsync(string capability, object input,
        CancellationToken cancellationToken)
    {
        return commands.ExecuteAsync(CliCommandClient.Request(capability, input),
            cancellationToken: cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
