using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;

namespace Igloo.Core.Runtime;

internal sealed record ObservedPayload(object Data, string Fingerprint);

/// <summary>A revision of observed facts, not exact physical target identity or execution approval.</summary>
public static class ObservedStateRevision
{
    public static string Fingerprint(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var facts = new
        {
            report.IsUefi, report.SecureBootEnabled, report.BitLocker, report.TotalRamBytes,
            Disks = report.Disks.OrderBy(d => d.DeviceId, StringComparer.Ordinal).Select(d => new
            {
                d.DeviceId, d.TotalBytes, d.FreeBytes, d.PartitionStyle,
                Partitions = d.Partitions.OrderBy(p => p.Index).Select(p => new
                {
                    p.Index, p.FileSystem, p.SizeBytes, p.IsBoot, p.IsSystem,
                    p.ShrinkableBytes, p.OffsetBytes, p.GptType,
                }),
            }),
            Findings = report.Findings.Select(f => new { f.Code, f.Severity })
                .OrderBy(f => f.Code, StringComparer.Ordinal),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(facts,
            CommandProtocol.JsonOptions)));
    }

    public static bool Matches(PreflightReport current, string expected) =>
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(Fingerprint(current), expected, StringComparison.Ordinal);
}

internal sealed class MigrationInspectHandler(IPreflightChecker checker) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("migration.inspect", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        var capacity = MigrationCapacityEvaluator.Evaluate(report.Disks, 0);
        var blockers = new List<string>();
        if (!report.IsUefi) blockers.Add("LegacyFirmware");
        if (capacity.State == MigrationCapacityState.AmbiguousTarget) blockers.Add("AmbiguousWindowsBootDisk");
        if (report.BitLocker is not BitLockerState.NotEncrypted) blockers.Add("BitLockerRequiresReview");
        return new ObservedPayload(new MigrationInspection(capacity.WindowsBootDiskCount,
            capacity.ObservedAvailableBytes, blockers,
            blockers.Count == 0), ObservedStateRevision.Fingerprint(report));
    }
}

internal sealed class MigrationPlanHandler(IPreflightChecker checker, DistroLoader loader, DistroRegistry registry,
    DistroCompatibilityEvaluator evaluator, int version = 1,
    IAdvisoryPlanJournal? planJournal = null) : ICommandHandler
{
    public CapabilityDescriptor Descriptor => new("migration.plan", version, 1, version,
        CommandKind.Plan, RiskClass.Low, false, AuthorityRequirement.User,
        loader.LoadedDistros.Where(d => d.IsAvailable && registry.TryGet(d.Id, out _))
            .Select(d => d.Id).Order(StringComparer.Ordinal).ToArray());

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        var input = request.Input.Deserialize<MigrationPlanInput>(CommandProtocol.JsonOptions);
        if (input is null || string.IsNullOrWhiteSpace(input.DistroId) || input.RequestedLinuxBytes < 0)
            throw new ArgumentException("Invalid migration plan input.");
        var manifest = loader.LoadedDistros.SingleOrDefault(d => d.IsAvailable &&
            string.Equals(d.Id, input.DistroId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException();
        if (!registry.TryGet(input.DistroId, out var plugin)) throw new KeyNotFoundException();
        var report = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        var required = Math.Max(input.RequestedLinuxBytes, plugin.Metadata.MinimumRequirements.MinDiskBytes);
        var capacity = MigrationCapacityEvaluator.Evaluate(report.Disks, required);
        var blockers = new List<string>();
        if (!report.IsUefi) blockers.Add("LegacyFirmware");
        if (report.BitLocker is not BitLockerState.NotEncrypted) blockers.Add("BitLockerRequiresReview");
        if (capacity.State == MigrationCapacityState.AmbiguousTarget) blockers.Add("AmbiguousWindowsBootDisk");
        if (evaluator.Evaluate(plugin.Id, report).Any(f => f.Severity == FindingSeverity.Blocker))
            blockers.Add("DistroCompatibilityBlocker");
        if (capacity.WindowsBootDiskCount == 1)
        {
            if (capacity.State == MigrationCapacityState.PhysicalDiskTooSmall)
                blockers.Add("DiskTooSmall");
            if (capacity.State == MigrationCapacityState.Unknown) blockers.Add("CapacityUnknown");
            if (capacity.State == MigrationCapacityState.Insufficient)
                blockers.Add("InsufficientObservedCapacity");
        }
        var observed = ObservedStateRevision.Fingerprint(report);
        var available = capacity.State == MigrationCapacityState.PhysicalDiskTooSmall
            ? null : capacity.ObservedAvailableBytes;
        if (version == 2)
        {
            if (planJournal is null) throw new InvalidOperationException("Plan journal is unavailable.");
            var providerRevision = await ProviderRevision.FingerprintAsync(plugin, manifest,
                cancellationToken)
                .ConfigureAwait(false);
            var boundRevision = MigrationStateRevision.Bind(observed, providerRevision, plugin.Id);
            var plan = new MigrationPlanResultV2(Guid.NewGuid(), plugin.Id, required,
                capacity.ObservedAvailableBytes,
                capacity.State switch
                {
                    MigrationCapacityState.KnownAvailable => MigrationCapacityDisposition.KnownAvailable,
                    MigrationCapacityState.Insufficient => MigrationCapacityDisposition.Insufficient,
                    MigrationCapacityState.PhysicalDiskTooSmall => MigrationCapacityDisposition.PhysicalDiskTooSmall,
                    MigrationCapacityState.AmbiguousTarget => MigrationCapacityDisposition.AmbiguousTarget,
                    _ => MigrationCapacityDisposition.Unknown,
                }, blockers, boundRevision, providerRevision, Executable: false,
                Disposition: "AdvisoryOnly", ObservedMachineFingerprint: observed);
            await planJournal.SaveAsync(plan, request, cancellationToken).ConfigureAwait(false);
            return new ObservedPayload(plan, boundRevision);
        }
        return new ObservedPayload(new MigrationPlanResult(plugin.Id, required,
            available,
            blockers, Executable: false, Disposition: "AdvisoryOnly"),
            observed);
    }
}

internal sealed class RecoveryInspectHandler : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("recovery.inspect", 1, 1, 1,
        CommandKind.Query, RiskClass.Low, false, AuthorityRequirement.User, []);

    public Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken) => Task.FromResult<object>(
            new RecoveryInspection("ObservationUnavailable", "ExactSnapshotNotCaptured", false));
}
