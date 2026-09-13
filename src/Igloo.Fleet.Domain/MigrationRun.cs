using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public enum MigrationState
{
    Discovered, Assessed, Eligible, Approved, Scheduled, Preparing, Prepared, Migrating,
    FirstBoot, Validating, Completed, Blocked, Failed, RecoveryRequired, Recovering,
    Recovered, Cancelled,
}

/// <summary>Lifecycle vocabulary; it grants no authority to execute endpoint operations.</summary>
public sealed record MigrationRun(Guid RunId, Guid DeviceId)
{
    public MigrationState State { get; private init; } = MigrationState.Discovered;

    public MigrationRun TransitionTo(MigrationState next)
    {
        var allowed = State switch
        {
            MigrationState.Discovered => next is MigrationState.Assessed or MigrationState.Cancelled,
            MigrationState.Assessed => next is MigrationState.Eligible or MigrationState.Blocked or MigrationState.Cancelled,
            MigrationState.Eligible => next is MigrationState.Approved or MigrationState.Blocked or MigrationState.Cancelled,
            MigrationState.Approved => next is MigrationState.Scheduled or MigrationState.Cancelled,
            MigrationState.Scheduled => next is MigrationState.Preparing or MigrationState.Cancelled,
            MigrationState.Preparing => next is MigrationState.Prepared or MigrationState.Failed or MigrationState.RecoveryRequired,
            MigrationState.Prepared => next is MigrationState.Migrating or MigrationState.Cancelled,
            MigrationState.Migrating => next is MigrationState.FirstBoot or MigrationState.Failed or MigrationState.RecoveryRequired,
            MigrationState.FirstBoot => next is MigrationState.Validating or MigrationState.RecoveryRequired,
            MigrationState.Validating => next is MigrationState.Completed or MigrationState.Failed or MigrationState.RecoveryRequired,
            MigrationState.Blocked => next is MigrationState.Assessed or MigrationState.Cancelled,
            MigrationState.Failed => next is MigrationState.RecoveryRequired or MigrationState.Cancelled,
            MigrationState.RecoveryRequired => next is MigrationState.Recovering,
            MigrationState.Recovering => next is MigrationState.Recovered or MigrationState.RecoveryRequired,
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException($"Illegal migration transition: {State} to {next}.");
        return this with { State = next };
    }
}
