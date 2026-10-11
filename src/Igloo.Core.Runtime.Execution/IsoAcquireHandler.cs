using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;

namespace Igloo.Core.Runtime.Execution;

public sealed class IsoAcquireHandler(DistroLoader loader, IsoAcquisitionCoordinator coordinator,
    IOperationJournal? journal = null, FileArtifactRegistry? artifacts = null,
    bool artifactMode = false) : ICommandHandler
{
    public CapabilityDescriptor Descriptor => new(artifactMode ? "artifact.acquire" : "iso.acquire", 1, 1, 1,
        CommandKind.Mutation, RiskClass.Low, false, AuthorityRequirement.User,
        loader.LoadedDistros.Where(d => d.IsAvailable).Select(d => d.Id)
            .Order(StringComparer.Ordinal).ToArray());

    public async Task<object> ExecuteAsync(CommandRequest request, IProgress<CommandProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Input.Deserialize<DistroSelection>(CommandProtocol.JsonOptions);
        if (string.IsNullOrWhiteSpace(selection?.DistroId)) throw new ArgumentException("Missing distro ID.");
        var distro = loader.LoadedDistros.SingleOrDefault(d => string.Equals(d.Id,
            selection.DistroId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException();
        using var lease = await ResourceLock.AcquireAsync("iso:" + distro.Id,
            cancellationToken).ConfigureAwait(false);
        if (journal is not null)
        {
            var targetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("iso:" + distro.Id)));
            await journal.CreateAsync(request, targetHash, cancellationToken).ConfigureAwait(false);
            await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Running,
                "iso.acquiring", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        var isoProgress = progress is null ? null : new Progress<IsoAcquisitionProgress>(p =>
            progress.Report(new(request.RequestId, request.OperationId, p.Phase.ToString(),
                p.BytesCompleted, p.BytesTotal)));
        try
        {
            var result = await coordinator.AcquireAsync(distro, isoProgress, cancellationToken).ConfigureAwait(false);
            ArtifactAcquired? artifact = null;
            if (artifactMode)
                artifact = await (artifacts ?? throw new InvalidOperationException(
                    "Artifact registry is unavailable.")).RecordAsync(distro.Id, result,
                    cancellationToken).ConfigureAwait(false);
            if (journal is not null)
                await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Completed,
                    "iso.verified", result.GpgVerified
                        ? ["Sha256Verified", "GpgVerified"] : ["Sha256Verified"],
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return artifact is not null ? artifact : (object)new IsoAcquired(distro.Id,
                result.Sha256Verified, result.GpgVerified, result.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            if (journal is not null)
                await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Cancelled,
                    "iso.cancelled", failureCode: "Cancelled", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (journal is not null)
                await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Failed,
                    "iso.failed", failureCode: "IsoAcquisitionFailed", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            throw;
        }
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
