using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

/// <summary>Only preflight and plugin compatibility are reachable. No installer/render/write interface is invoked.</summary>
public sealed class ReadOnlyPlanner(IPreflightChecker checker, DistroRegistry registry)
{
    public async Task<ReadOnlyWorkResult> RunAsync(ReadOnlyWorkItem work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.PayloadVersion != 1 || !Enum.IsDefined(work.Type) || work.LeaseId is null ||
            (work.Type == ReadOnlyWorkType.RunMigrationDryRun && work.Profile?.SchemaVersion != 1))
            throw new InvalidDataException("Unsupported read-only work.");
        var started = DateTimeOffset.UtcNow;
        try { return await ObserveAsync(work, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            var failed = new AssessmentResult(FleetProtocolVersion.Current, work.WorkItemId, work.Identity,
                started, DateTimeOffset.UtcNow, ReadOnlyAssessment.AgentVersion, ReadOnlyAssessment.IglooVersion,
                1, new(0, 0), [], EligibilityStatus.NeedsReview, AssessmentOutcome.LocalExecutionFailed);
            return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId.Value, work.CorrelationId,
                work.Profile?.RevisionId, failed, null);
        }
    }

    private async Task<ReadOnlyWorkResult> ObserveAsync(ReadOnlyWorkItem work, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var report = await checker.RunAsync(ct).ConfigureAwait(false);
        var assessment = ReadOnlyAssessment.Map(report, work.Identity, started) with { AssessmentId = work.WorkItemId };
        DryRunFacts? facts = null;
        if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
        {
            var spec = work.Profile!.Spec;
            var reasons = new HashSet<PlanningReason>();
            var supported = registry.TryGet(spec.DistroId, out var plugin) && Igloo.Fleet.Domain.PlanningService.SupportedDistro(spec.DistroId);
            var required = spec.MinimumAvailableBytes;
            string? pluginHash = null;
            if (supported)
            {
                required = Math.Max(required, plugin!.Metadata.MinimumRequirements.MinDiskBytes);
                // Only this read-only plugin method is called. No rendering or payload staging.
                var findings = plugin.CheckCompatibility(report);
                if (findings.Any(f => f.Severity == FindingSeverity.Blocker)) reasons.Add(PlanningReason.HardwareUnsupported);
                if (findings.Any(f => f.Severity == FindingSeverity.Warning)) reasons.Add(PlanningReason.CompatibilityReview);
                // Hash both implementation and local catalog input because plugin metadata influences compatibility.
                var assemblyPath = plugin.GetType().Assembly.Location;
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hasher.AppendData(await File.ReadAllBytesAsync(assemblyPath, ct).ConfigureAwait(false));
                var catalog = Path.Join(Path.GetDirectoryName(assemblyPath), "distro.json");
                if (File.Exists(catalog))
                    hasher.AppendData(await File.ReadAllBytesAsync(catalog, ct).ConfigureAwait(false));
                pluginHash = Convert.ToHexString(hasher.GetHashAndReset());
            }
            else reasons.Add(PlanningReason.UnsupportedDistro);
            // A deterministic candidate: the single disk with Windows' boot partition.
            // GetSupportedSize values are observations, not permission to shrink.
            var available = AvailableCapacity(report.Disks, required);
            facts = new(supported, report.SecureBootEnabled, available, required, pluginHash, [.. reasons.Order()]);
        }
        return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value, work.CorrelationId,
            work.Profile?.RevisionId, assessment, facts);
    }

    public static long? AvailableCapacity(IReadOnlyList<DiskInfo> disks, long required)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var candidates = disks.Where(d => d.Partitions.Any(p => p.IsBoot)).ToArray();
        if (candidates.Length != 1) return null;
        var disk = candidates[0];
        if (disk.TotalBytes <= 0) return null;
        if (disk.TotalBytes < required) return disk.TotalBytes; // Definitive physical upper bound.
        var shrinkable = disk.Partitions.Where(p => p.IsBoot).Select(p => p.ShrinkableBytes).DefaultIfEmpty(0).Max();
        // Zero means either no shrink capacity OR a failed GetSupportedSize query in shared Preflight.
        // A small unallocated region does not prove the Windows partition cannot release more space.
        if (disk.FreeBytes < required && shrinkable <= 0) return null;
        return Math.Max(disk.FreeBytes, shrinkable);
    }
}
