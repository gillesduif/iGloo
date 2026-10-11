using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Preflight;

namespace Igloo.Core.Runtime.Execution;

public sealed record AllocationPartitionIdentity(Guid PartitionGuid, Guid PartitionType,
    ulong OffsetBytes, ulong SizeBytes);
public sealed record CanonicalAllocationObservation(CanonicalVolumeIdentityV1 WindowsVolume,
    IReadOnlyList<AllocationPartitionIdentity> Partitions, SupportedSizeObservation SupportedSize);

public interface IAllocationObservationSource
{
    CanonicalAllocationObservation? Observe(CanonicalVolumeIdentityV1 target);
}

/// <summary>Reads the whole disk layout and the exact NTFS partition's supported sizes.</summary>
public sealed class WindowsAllocationObservationSource(IWindowsStorageReader storage,
    ICanonicalMigrationTargetSource targets) : IAllocationObservationSource
{
    public CanonicalAllocationObservation? Observe(CanonicalVolumeIdentityV1 target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!CanonicalRecoveryIdentity.IsValid(target)) return null;
        var current = targets.Observe();
        if (current?.WindowsVolume != target) return null;
        var inventory = storage.ReadIdentitySnapshot();
        if (inventory.Availability != ObservationAvailability.Available ||
            CanonicalRecoveryIdentity.Bind(inventory, target.VolumeGuid) is not
                { Availability: ObservationAvailability.Available } bound || bound.Value != target ||
            inventory.Value.Partitions.Any(p => p.DiskNumber.Availability != ObservationAvailability.Available))
            return null;
        var onDisk = inventory.Value.Partitions.Where(p =>
            p.DiskNumber.Value == current.ObservedDiskNumber).ToArray();
        if (onDisk.Length == 0) return null;
        var layout = new List<AllocationPartitionIdentity>(onDisk.Length);
        foreach (var partition in onDisk)
        {
            if (partition.PartitionGuid.Availability != ObservationAvailability.Available ||
                partition.GptType.Availability != ObservationAvailability.Available ||
                partition.Offset.Availability != ObservationAvailability.Available ||
                partition.Size.Availability != ObservationAvailability.Available)
                return null;
            layout.Add(new(partition.PartitionGuid.Value, partition.GptType.Value,
                partition.Offset.Value, partition.Size.Value));
        }
        var owners = onDisk.Where(p => p.PartitionGuid.Value == target.PartitionGuid &&
            p.Number.Availability == ObservationAvailability.Available).ToArray();
        if (owners.Length != 1 || current.ObservedDiskNumber > int.MaxValue ||
            owners[0].Number.Value > int.MaxValue) return null;
        var rows = storage.ReadPartitions((int)current.ObservedDiskNumber,
            (int)owners[0].Number.Value);
        if (rows.Availability != ObservationAvailability.Available || rows.Rows.Count != 1 ||
            string.IsNullOrWhiteSpace(rows.Rows[0].ObjectPath)) return null;
        var row = StorageObservationProjection.Partition(rows.Rows[0]);
        if (row.PartitionGuid.Availability != ObservationAvailability.Available ||
            row.PartitionGuid.Value != target.PartitionGuid ||
            row.GptType.Availability != ObservationAvailability.Available ||
            row.GptType.Value != target.PartitionType ||
            row.DiskNumber.Availability != ObservationAvailability.Available ||
            row.DiskNumber.Value != current.ObservedDiskNumber ||
            row.Number.Availability != ObservationAvailability.Available ||
            row.Number.Value != owners[0].Number.Value ||
            row.Offset.Availability != ObservationAvailability.Available ||
            row.Offset.Value != target.OffsetBytes ||
            row.Size.Availability != ObservationAvailability.Available ||
            row.Size.Value != target.SizeBytes) return null;
        var supported = StorageObservationProjection.SupportedSize(
            storage.ReadSupportedSize(rows.Rows[0]));
        return supported.Availability == ObservationAvailability.Available
            ? new(target, layout, supported.Value) : null;
    }
}

/// <summary>Advisory Windows NTFS shrink identity; it does not bind installer partitions.</summary>
public sealed class FileMigrationAllocationRegistry(string directory,
    FileMigrationTargetRegistry targets, IAllocationObservationSource source)
{
    private const ulong MiB = 1024 * 1024;
    public static string DefaultDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "allocations");

    public async Task<MigrationAllocationResult> RecordAsync(Guid planId, Guid targetId,
        string planFingerprint, CanonicalAllocationObservation observed, long requestedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var proposal = Propose(observed, requestedBytes);
        if (planId == Guid.Empty || targetId == Guid.Empty || !IsDigest(planFingerprint))
            throw new CommandPreconditionException();
        var id = Guid.NewGuid();
        var record = new AllocationRecord(1, id, planId, targetId, planFingerprint,
            LayoutRevision(observed), requestedBytes, proposal.PlannedShrinkBytes,
            proposal.ResultingPartitionBytes, proposal.NewFreeOffsetBytes, DateTimeOffset.UtcNow, null);
        record = record with { IntegritySha256 = Integrity(record) };
        Directory.CreateDirectory(directory);
        var path = RecordPath(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, record, CommandProtocol.JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Public(record, observed);
    }

    public async Task<MigrationAllocationResult> ResolveAsync(Guid allocationId, Guid planId,
        Guid targetId, CancellationToken cancellationToken = default)
    {
        if (allocationId == Guid.Empty || planId == Guid.Empty || targetId == Guid.Empty)
            throw new ArgumentException("Allocation, plan and target IDs are required.");
        var record = await ReadAsync(allocationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException();
        if (record.PlanId != planId || record.TargetId != targetId)
            throw new StateChangedException();
        var target = await targets.ResolveAsync(targetId, planId, record.PlanFingerprint,
            cancellationToken).ConfigureAwait(false);
        var first = source.Observe(target) ?? throw new CommandPreconditionException();
        var second = source.Observe(target) ?? throw new CommandPreconditionException();
        if (!string.Equals(LayoutRevision(first), record.LayoutRevision, StringComparison.Ordinal) ||
            !string.Equals(LayoutRevision(second), record.LayoutRevision, StringComparison.Ordinal))
            throw new StateChangedException();
        var proposal = Propose(second, record.RequestedShrinkBytes);
        if (proposal.PlannedShrinkBytes != record.PlannedShrinkBytes ||
            proposal.ResultingPartitionBytes != record.ResultingPartitionBytes ||
            proposal.NewFreeOffsetBytes != record.NewFreeOffsetBytes)
            throw new StateChangedException();
        return Public(record, second);
    }

    private async Task<AllocationRecord?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        AllocationRecord? record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<AllocationRecord>(stream,
                CommandProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw new CommandEvidenceException(); }
        if (record is null || record.SchemaVersion != 1 || record.AllocationId != id ||
            record.PlanId == Guid.Empty || record.TargetId == Guid.Empty ||
            !IsDigest(record.PlanFingerprint) || !IsDigest(record.LayoutRevision) ||
            record.RequestedShrinkBytes <= 0 || record.PlannedShrinkBytes <= 0 ||
            record.ResultingPartitionBytes <= 0 || record.NewFreeOffsetBytes <= 0 ||
            !string.Equals(record.IntegritySha256,
                Integrity(record with { IntegritySha256 = null }), StringComparison.Ordinal))
            throw new CommandEvidenceException();
        return record;
    }

    private static ShrinkProposal Propose(CanonicalAllocationObservation observed, long requestedBytes)
    {
        if (requestedBytes <= 0 || !Valid(observed)) throw new CommandPreconditionException();
        var volume = observed.WindowsVolume;
        var supported = observed.SupportedSize;
        if (supported.Maximum != volume.SizeBytes || supported.Minimum >= volume.SizeBytes ||
            (ulong)requestedBytes > volume.SizeBytes - supported.Minimum)
            throw new CommandPreconditionException();
        try
        {
            checked
            {
                var resulting = ((volume.SizeBytes - (ulong)requestedBytes) / MiB) * MiB;
                if (resulting < supported.Minimum || resulting == 0)
                    throw new CommandPreconditionException();
                var actual = volume.SizeBytes - resulting;
                return new((long)actual, (long)resulting,
                    (long)(volume.OffsetBytes + resulting));
            }
        }
        catch (OverflowException) { throw new CommandPreconditionException(); }
    }

    private static bool Valid(CanonicalAllocationObservation? observed)
    {
        if (observed is null || !CanonicalRecoveryIdentity.IsValid(observed.WindowsVolume) ||
            !string.Equals(observed.WindowsVolume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            observed.Partitions.Count == 0) return false;
        var disk = observed.WindowsVolume.Disk;
        if (disk.SizeBytes > long.MaxValue) return false;
        var ordered = observed.Partitions.OrderBy(p => p.OffsetBytes).ToArray();
        if (ordered.Select(p => p.PartitionGuid).Distinct().Count() != ordered.Length ||
            ordered.Count(p => p.PartitionGuid == observed.WindowsVolume.PartitionGuid &&
                p.PartitionType == observed.WindowsVolume.PartitionType &&
                p.OffsetBytes == observed.WindowsVolume.OffsetBytes &&
                p.SizeBytes == observed.WindowsVolume.SizeBytes) != 1) return false;
        ulong end = 0;
        foreach (var part in ordered)
        {
            if (part.PartitionGuid == Guid.Empty || part.PartitionType == Guid.Empty ||
                part.SizeBytes == 0 || part.OffsetBytes < end || part.OffsetBytes > disk.SizeBytes ||
                part.SizeBytes > disk.SizeBytes - part.OffsetBytes) return false;
            end = part.OffsetBytes + part.SizeBytes;
        }
        return true;
    }

    internal static string LayoutRevision(CanonicalAllocationObservation observed)
    {
        if (!Valid(observed)) throw new CommandPreconditionException();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            observed.WindowsVolume,
            Partitions = observed.Partitions.OrderBy(p => p.OffsetBytes)
                .ThenBy(p => p.PartitionGuid).ToArray(),
            observed.SupportedSize.Minimum, observed.SupportedSize.Maximum,
        }, CommandProtocol.JsonOptions)));
    }

    private static MigrationAllocationResult Public(AllocationRecord record,
        CanonicalAllocationObservation observed) => new(record.PlanId, record.TargetId,
        record.AllocationId, "WindowsBootNtfsShrinkIntentV1", record.RequestedShrinkBytes,
        record.PlannedShrinkBytes, checked((long)observed.WindowsVolume.SizeBytes),
        record.ResultingPartitionBytes, record.NewFreeOffsetBytes, record.LayoutRevision,
        MigrationCapacityDisposition.KnownAvailable, ["InstallerLayoutNotBound"], false);

    private string RecordPath(Guid id) => Path.Join(directory, id.ToString("N") + ".json");
    private static string Integrity(AllocationRecord record) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(record, CommandProtocol.JsonOptions)));
    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed record ShrinkProposal(long PlannedShrinkBytes, long ResultingPartitionBytes,
        long NewFreeOffsetBytes);
    private sealed record AllocationRecord(int SchemaVersion, Guid AllocationId,
        Guid PlanId, Guid TargetId, string PlanFingerprint, string LayoutRevision,
        long RequestedShrinkBytes, long PlannedShrinkBytes, long ResultingPartitionBytes,
        long NewFreeOffsetBytes, DateTimeOffset IssuedAt, string? IntegritySha256);
}

public sealed class MigrationTargetAllocateHandler(IAdvisoryPlanJournal plans,
    FileMigrationTargetRegistry targets, FileMigrationAllocationRegistry allocations,
    IAllocationObservationSource source, IPreflightChecker checker, DistroLoader loader,
    DistroRegistry providers) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("migration.target.allocate", 1, 1, 1,
        CommandKind.Plan, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input.Deserialize<MigrationAllocationInput>(CommandProtocol.JsonOptions);
        if (input is null || input.PlanId == Guid.Empty || input.TargetId == Guid.Empty ||
            input.RequestedShrinkBytes <= 0) throw new ArgumentException("Valid allocation input is required.");
        var plan = await plans.ReadAsync(input.PlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException();
        if (plan.BlockerCodes.Count != 0 || plan.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            plan.Executable || plan.Disposition != "AdvisoryOnly" ||
            input.RequestedShrinkBytes < plan.RequiredBytes)
            throw new CommandPreconditionException();
        var manifest = loader.LoadedDistros.SingleOrDefault(d => d.IsAvailable &&
            string.Equals(d.Id, plan.DistroId, StringComparison.OrdinalIgnoreCase))
            ?? throw new StateChangedException();
        if (!providers.TryGet(plan.DistroId, out var plugin)) throw new StateChangedException();
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, manifest,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(providerRevision, plan.ProviderRevisionFingerprint,
            StringComparison.Ordinal)) throw new StateChangedException();
        var before = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!ObservedStateRevision.Matches(before, plan.ObservedMachineFingerprint))
            throw new StateChangedException();
        var target = await targets.ResolveAsync(input.TargetId, plan.PlanId,
            plan.ObservedStateFingerprint, cancellationToken).ConfigureAwait(false);
        var first = source.Observe(target) ?? throw new CommandPreconditionException();
        var second = source.Observe(target) ?? throw new CommandPreconditionException();
        var after = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!ObservedStateRevision.Matches(after, plan.ObservedMachineFingerprint) ||
            !string.Equals(FileMigrationAllocationRegistry.LayoutRevision(first),
                FileMigrationAllocationRegistry.LayoutRevision(second), StringComparison.Ordinal))
            throw new StateChangedException();
        return await allocations.RecordAsync(plan.PlanId, input.TargetId,
            plan.ObservedStateFingerprint, second, input.RequestedShrinkBytes,
            cancellationToken).ConfigureAwait(false);
    }
}
