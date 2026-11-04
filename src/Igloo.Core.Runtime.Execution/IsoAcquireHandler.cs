using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;

namespace Igloo.Core.Runtime.Execution;

public sealed class IsoAcquireHandler(DistroLoader loader, IsoAcquisitionCoordinator coordinator,
    IOperationJournal? journal = null, FileArtifactRegistry? artifacts = null,
    bool artifactMode = false, int version = 1) : ICommandHandler, IDurableCommandAdmission
{
    public CapabilityDescriptor Descriptor => new(artifactMode ? "artifact.acquire" : "iso.acquire",
        version, 1, version,
        CommandKind.Mutation, RiskClass.Low, false, AuthorityRequirement.User,
        loader.LoadedDistros.Where(d => d.IsAvailable).Select(d => d.Id)
            .Order(StringComparer.Ordinal).ToArray());

    public async Task AdmitAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (journal is null)
            throw new CommandPreconditionException("ISO acquisition requires a durable operation journal.");
        var distro = SelectDistro(request);
        var targetHash = TargetIdentityHash(distro.Id);
        // A restarted frontend may have lost the prior request ID. The Core
        // journal remains authoritative and closes that duplicate-admission gap
        // for CLI and every frontend, not just the Community ISO screen.
        using var lease = await ResourceLock.AcquireAsync("iso-admit:" + distro.Id,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LocalOperationRecord> prior;
        try
        {
            var iso = await journal.FindAsync("iso.acquire", targetHash, cancellationToken)
                .ConfigureAwait(false);
            var artifacts = await journal.FindAsync("artifact.acquire", targetHash,
                cancellationToken).ConfigureAwait(false);
            prior = iso.Concat(artifacts).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OperationJournalUnavailableException("ISO operation journal lookup failed.", ex);
        }
        if (prior.Any(record => record.State is not (LocalOperationState.Completed
                or LocalOperationState.Failed or LocalOperationState.Cancelled)))
            throw new OperationJournalUnavailableException(
                "An earlier ISO operation needs a status review before retrying.");
        await journal.CreateAsync(request, targetHash, cancellationToken).ConfigureAwait(false);
    }

    internal static string TargetIdentityHash(string distroId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("iso:" + distroId)));

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isoProgress = progress is null ? null : new Progress<IsoAcquisitionProgress>(p =>
            progress.Report(new(request.RequestId, request.OperationId, p.Phase.ToString(),
                p.BytesCompleted, p.BytesTotal)));
        try
        {
            var distro = SelectDistro(request);
            using var lease = await ResourceLock.AcquireAsync("iso:" + distro.Id,
                cancellationToken).ConfigureAwait(false);
            if (journal is not null)
                await TransitionRunningAsync(request.OperationId!.Value, cancellationToken)
                    .ConfigureAwait(false);
            var result = await AcquireClassifiedAsync(distro, isoProgress, cancellationToken)
                .ConfigureAwait(false);
            if (RequiresGpgVerification(distro.Iso) && !result.GpgVerified)
                throw new IsoAcquisitionFailureException(IsoAcquisitionFailureKind.VerificationFailed,
                    "The declared installer-image signature was not verified.");
            if (!result.Sha256Verified)
                throw new IsoAcquisitionFailureException(IsoAcquisitionFailureKind.VerificationFailed,
                    "The acquired image has no verified SHA-256 identity.");
            if (version == 2 && (result.VerifiedSha256 is not { Length: 64 } digest ||
                !digest.All(Uri.IsHexDigit) || result.SizeBytes <= 0 ||
                !Path.IsPathFullyQualified(result.LocalPath)))
                throw new IsoAcquisitionFailureException(IsoAcquisitionFailureKind.VerificationFailed,
                    "The acquired image has no verified SHA-256 identity.");
            ArtifactAcquired? artifact = null;
            if (artifactMode)
                artifact = await (artifacts ?? throw new InvalidOperationException(
                    "Artifact registry is unavailable.")).RecordAsync(distro.Id, result,
                    cancellationToken).ConfigureAwait(false);
            if (journal is not null)
                await TransitionTerminalAsync(request.OperationId!.Value, LocalOperationState.Completed,
                    "iso.verified", result.GpgVerified
                        ? ["Sha256Verified", "GpgVerified"] : ["Sha256Verified"])
                    .ConfigureAwait(false);
            if (artifact is not null) return artifact;
            if (version == 2)
                return new IsoAcquiredV2(distro.Id, result.LocalPath, result.VerifiedSha256!,
                    result.Sha256Verified, result.GpgVerified, result.SizeBytes);
            return new IsoAcquired(distro.Id, result.Sha256Verified, result.GpgVerified,
                result.SizeBytes);
        }
        catch (OperationJournalUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (journal is not null)
                await TransitionTerminalAsync(request.OperationId!.Value, LocalOperationState.Cancelled,
                    "iso.cancelled", failureCode: "Cancelled")
                    .ConfigureAwait(false);
            throw;
        }
        catch (ResourceBusyException)
        {
            if (journal is not null)
                await TransitionTerminalAsync(request.OperationId!.Value, LocalOperationState.Failed,
                    "iso.failed", failureCode: "ResourceBusy").ConfigureAwait(false);
            throw;
        }
        catch (IsoAcquisitionFailureException ex)
        {
            if (journal is not null)
                await TransitionTerminalAsync(request.OperationId!.Value, LocalOperationState.Failed,
                    "iso.failed", failureCode: FailureCode(ex.Kind)).ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (journal is not null)
                await TransitionTerminalAsync(request.OperationId!.Value, LocalOperationState.Failed,
                    "iso.failed", failureCode: "IsoAcquisitionFailed")
                    .ConfigureAwait(false);
            throw;
        }
    }

    private DistroManifest SelectDistro(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<DistroSelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(selection?.DistroId)) throw new ArgumentException("Missing distro ID.");
        return loader.LoadedDistros.SingleOrDefault(d => string.Equals(d.Id,
            selection.DistroId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException();
    }

    private static bool RequiresGpgVerification(DistroIsoSpec spec) =>
        spec.GpgSignatureUrl is not null || spec.GpgSignedDataUrl is not null ||
        spec.GpgKeyUrl is not null || !string.IsNullOrWhiteSpace(spec.GpgKeyFile) ||
        !string.IsNullOrWhiteSpace(spec.GpgKeyFingerprint);

    private async Task<IsoAcquisitionResult> AcquireClassifiedAsync(DistroManifest distro,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return await coordinator.AcquireAsync(distro, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IsoAcquisitionFailureException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   OperationCanceledException)
        {
            throw new IsoAcquisitionFailureException(IsoAcquisitionFailureKind.NetworkUnavailable,
                "The distribution image or verification data could not be fetched.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IsoAcquisitionFailureException(IsoAcquisitionFailureKind.CacheUnavailable,
                "The local image cache could not be used.", ex);
        }
    }

    private static string FailureCode(IsoAcquisitionFailureKind kind) => kind switch
    {
        IsoAcquisitionFailureKind.NetworkUnavailable => "IsoNetworkUnavailable",
        IsoAcquisitionFailureKind.CacheUnavailable => "IsoCacheUnavailable",
        IsoAcquisitionFailureKind.VerificationFailed => "IsoVerificationFailed",
        IsoAcquisitionFailureKind.SourceMetadataUnavailable => "IsoSourceMetadataUnavailable",
        _ => "IsoAcquisitionFailed",
    };

    private async Task TransitionRunningAsync(Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            await journal!.TransitionAsync(operationId, LocalOperationState.Running,
                "iso.acquiring", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OperationJournalUnavailableException("ISO operation journal update failed.", ex);
        }
    }

    private async Task TransitionTerminalAsync(Guid operationId, LocalOperationState state,
        string stage, IReadOnlyList<string>? evidenceCodes = null, string? failureCode = null)
    {
        try
        {
            await journal!.TransitionAsync(operationId, state, stage, evidenceCodes,
                failureCode: failureCode, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OperationJournalUnavailableException("ISO operation outcome could not be persisted.", ex);
        }
    }
}

/// <summary>Recovers the Core journal identities for one selected installation image.</summary>
public sealed class IsoOperationsHandler(DistroLoader loader, IOperationJournal journal) : ICommandHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("iso.operations", 1, 1, 1,
        CommandKind.Query, RiskClass.None, false, AuthorityRequirement.User,
        loader.LoadedDistros.Where(d => d.IsAvailable).Select(d => d.Id)
            .Order(StringComparer.Ordinal).ToArray());

    public async Task<object> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<DistroSelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(selection?.DistroId))
            throw new ArgumentException("Missing distro ID.");
        var distro = loader.LoadedDistros.SingleOrDefault(d => d.IsAvailable &&
            string.Equals(d.Id, selection.DistroId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException();
        var iso = await journal.FindAsync("iso.acquire",
            IsoAcquireHandler.TargetIdentityHash(distro.Id), cancellationToken)
            .ConfigureAwait(false);
        var artifacts = await journal.FindAsync("artifact.acquire",
            IsoAcquireHandler.TargetIdentityHash(distro.Id), cancellationToken)
            .ConfigureAwait(false);
        return new IsoOperations(distro.Id, iso.Concat(artifacts)
            .OrderByDescending(record => record.CreatedAt).Where(record => record.State is not
            (LocalOperationState.Completed or LocalOperationState.Failed or
             LocalOperationState.Cancelled)).Select(record => new OperationStatus(
            record.OperationId, record.CapabilityId, record.State.ToString(),
            record.CreatedAt, record.UpdatedAt, record.CurrentStage,
            record.EvidenceCodes, record.FailureCode, record.ContinuationCode)).ToArray());
    }
}

/// <summary>Cross-process, non-blocking lease for a named local resource.</summary>
public static class ResourceLock
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The returned lease is owned and disposed by the caller.")]
    public static ValueTask<IDisposable> AcquireAsync(string resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Acquire(resource));
    }

    public static IDisposable AcquireMany(IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var leases = new List<IDisposable>();
        try
        {
            foreach (var resource in resources.Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
                leases.Add(Acquire(resource));
            return new LeaseSet(leases);
        }
        catch
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The returned lease set is owned and disposed by the caller.")]
    public static ValueTask<IDisposable> AcquireManyAsync(IEnumerable<string> resources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AcquireMany(resources));
    }

    public static IDisposable Acquire(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        var directory = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "command-locks");
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Join(directory, hash + ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new ResourceBusyException();
        }
    }

    private sealed class LeaseSet(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
            leases.Clear();
        }
    }
}
