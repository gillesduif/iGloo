using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Preflight;

namespace Igloo.Core.Runtime.Execution;

/// <summary>Number correlates two observations; only the canonical identity is retained as authority.</summary>
public sealed record CanonicalMigrationTarget(CanonicalVolumeIdentityV1 WindowsVolume,
    uint ObservedDiskNumber);

public interface ICanonicalMigrationTargetSource
{
    CanonicalMigrationTarget? Observe();
}

/// <summary>Read-only Windows identity acquisition. Reduced or ambiguous storage identity fails closed.</summary>
public sealed class WindowsCanonicalMigrationTargetSource : ICanonicalMigrationTargetSource
{
    private readonly IWindowsStorageReader _storage;
    private readonly Func<Observation<Guid>> _windowsVolume;

    public WindowsCanonicalMigrationTargetSource(IWindowsStorageReader storage)
        : this(storage, WindowsRecoveryPathReader.ReadWindowsVolume) { }

    internal WindowsCanonicalMigrationTargetSource(IWindowsStorageReader storage,
        Func<Observation<Guid>> windowsVolume)
    { _storage = storage; _windowsVolume = windowsVolume; }

    public CanonicalMigrationTarget? Observe()
    {
        var windows = _windowsVolume();
        if (windows.Availability != ObservationAvailability.Available) return null;
        var inventory = _storage.ReadIdentitySnapshot();
        if (inventory.Availability != ObservationAvailability.Available) return null;
        var identity = CanonicalRecoveryIdentity.Bind(inventory, windows.Value);
        if (identity.Availability != ObservationAvailability.Available ||
            !string.Equals(identity.Value.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            return null;
        var owners = inventory.Value.Partitions.Where(p =>
            p.PartitionGuid.Availability == ObservationAvailability.Available &&
            p.PartitionGuid.Value == identity.Value.PartitionGuid).ToArray();
        if (owners.Length != 1 || owners[0].IsBoot.Availability != ObservationAvailability.Available ||
            !owners[0].IsBoot.Value ||
            owners[0].DiskNumber.Availability != ObservationAvailability.Available)
            return null;
        return new(identity.Value, owners[0].DiskNumber.Value);
    }
}

/// <summary>Private opaque-ID registry. It persists no raw disk identifier or secret.</summary>
public sealed class FileMigrationTargetRegistry(string directory, ICanonicalMigrationTargetSource source)
{
    public static string DefaultDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "targets");

    public async Task<MigrationTargetBinding> RecordAsync(MigrationPlanResultV2 plan,
        CanonicalMigrationTarget observed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observed);
        if (plan.PlanId == Guid.Empty || !IsDigest(plan.ObservedStateFingerprint) ||
            !CanonicalRecoveryIdentity.IsValid(observed.WindowsVolume))
            throw new CommandPreconditionException();
        var id = Guid.NewGuid();
        var record = new TargetRecord(1, id, plan.PlanId, plan.ObservedStateFingerprint,
            Fingerprint(observed.WindowsVolume), DateTimeOffset.UtcNow, null);
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
            File.Move(temporary, path); // Never replace another issued ID.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(plan.PlanId, id, plan.ObservedStateFingerprint, Executable: false);
    }

    /// <summary>Future prepare/execute callers must additionally recheck plan, capacity, provider and recovery.</summary>
    public async Task<CanonicalVolumeIdentityV1> ResolveAsync(Guid targetId, Guid planId,
        string boundStateFingerprint, CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty || planId == Guid.Empty || !IsDigest(boundStateFingerprint))
            throw new ArgumentException("A bound target and plan are required.");
        var record = await ReadAsync(targetId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException();
        if (record.PlanId != planId ||
            !string.Equals(record.BoundStateFingerprint, boundStateFingerprint,
                StringComparison.Ordinal))
            throw new StateChangedException();
        var first = source.Observe() ?? throw new CommandPreconditionException();
        var second = source.Observe() ?? throw new CommandPreconditionException();
        if (!CanonicalRecoveryIdentity.IsValid(first.WindowsVolume) ||
            !CanonicalRecoveryIdentity.IsValid(second.WindowsVolume) ||
            first.ObservedDiskNumber != second.ObservedDiskNumber ||
            !string.Equals(Fingerprint(first.WindowsVolume), record.CanonicalIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(Fingerprint(second.WindowsVolume), record.CanonicalIdentitySha256,
                StringComparison.Ordinal))
            throw new StateChangedException();
        return second.WindowsVolume;
    }

    private async Task<TargetRecord?> ReadAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var path = RecordPath(targetId);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        TargetRecord? record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<TargetRecord>(stream,
                CommandProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw new CommandEvidenceException(); }
        if (record is null || record.SchemaVersion != 1 || record.TargetId != targetId ||
            record.PlanId == Guid.Empty || !IsDigest(record.BoundStateFingerprint) ||
            !IsDigest(record.CanonicalIdentitySha256) ||
            !string.Equals(record.IntegritySha256,
                Integrity(record with { IntegritySha256 = null }), StringComparison.Ordinal))
            throw new CommandEvidenceException();
        return record;
    }

    private string RecordPath(Guid id) => Path.Join(directory, id.ToString("N") + ".json");

    private static string Fingerprint(CanonicalVolumeIdentityV1 identity) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity,
            CommandProtocol.JsonOptions)));

    private static string Integrity(TargetRecord record) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(record, CommandProtocol.JsonOptions)));

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private sealed record TargetRecord(int SchemaVersion, Guid TargetId, Guid PlanId,
        string BoundStateFingerprint, string CanonicalIdentitySha256,
        DateTimeOffset IssuedAt, string? IntegritySha256);
}

/// <summary>Advisory target binding for the single Windows boot disk only.</summary>
public sealed class MigrationTargetBindHandler(IAdvisoryPlanJournal plans,
    IPreflightChecker checker, DistroLoader loader, DistroRegistry providers,
    ICanonicalMigrationTargetSource source, FileMigrationTargetRegistry targets) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("migration.target.bind", 1, 1, 1,
        CommandKind.Plan, RiskClass.Low, false, AuthorityRequirement.User, []);

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input.Deserialize<PlanSelection>(CommandProtocol.JsonOptions);
        if (input is null || input.PlanId == Guid.Empty)
            throw new ArgumentException("Plan ID is required.");
        var plan = await plans.ReadAsync(input.PlanId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException();
        if (plan.BlockerCodes.Count != 0 ||
            plan.CapacityState != MigrationCapacityDisposition.KnownAvailable ||
            plan.Executable || plan.Disposition != "AdvisoryOnly")
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
        var observed = source.Observe() ?? throw new CommandPreconditionException();
        if (!MatchesBootDisk(before, observed)) throw new CommandPreconditionException();
        var repeated = source.Observe() ?? throw new CommandPreconditionException();
        if (observed != repeated) throw new StateChangedException();
        var after = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!ObservedStateRevision.Matches(after, plan.ObservedMachineFingerprint))
            throw new StateChangedException();
        return await targets.RecordAsync(plan, repeated, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesBootDisk(PreflightReport report, CanonicalMigrationTarget observed)
    {
        var candidates = report.Disks.Where(d => d.Partitions.Any(p => p.IsBoot)).ToArray();
        if (candidates.Length != 1 || !CanonicalRecoveryIdentity.IsValid(observed.WindowsVolume))
            return false;
        var disk = candidates[0];
        const string prefix = @"\\.\PHYSICALDRIVE";
        if (!disk.DeviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(disk.DeviceId.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) ||
            number != observed.ObservedDiskNumber || disk.TotalBytes <= 0 ||
            (ulong)disk.TotalBytes != observed.WindowsVolume.Disk.SizeBytes)
            return false;
        var boot = disk.Partitions.Where(p => p.IsBoot).ToArray();
        return boot.Length == 1 && boot[0].OffsetBytes >= 0 && boot[0].SizeBytes > 0 &&
            (ulong)boot[0].OffsetBytes == observed.WindowsVolume.OffsetBytes &&
            (ulong)boot[0].SizeBytes == observed.WindowsVolume.SizeBytes;
    }
}
