using System.IO;
using System.Text.Json;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;

namespace Igloo.Community.App;

/// <summary>The Community image step uses the same packaged CLI command as other frontends.</summary>
internal sealed class CliIsoWorkflow(CliCommandClient client) : IIsoWorkflow, IIsoOperationStatusWorkflow
{
    public async Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distro);
        var commandProgress = progress is null ? null : new Progress<CommandProgress>(p =>
        {
            if (Enum.TryParse<IsoAcquisitionPhase>(p.Phase, out var phase) &&
                Enum.IsDefined(phase))
                progress.Report(new IsoAcquisitionProgress(phase, p.Completed ?? 0,
                    p.Total, null));
        });
        // Keep an identity even if the transport ends before the first progress
        // frame. Core admits this identity durably before executing acquisition.
        var request = CliCommandClient.Request("iso.acquire",
            new DistroSelection(distro.Id), capabilityVersion: 2) with
        {
            OperationId = Guid.NewGuid(),
        };
        var result = await client.ExecuteAsync(request, commandProgress,
            cancellationToken).ConfigureAwait(false);
        if (result.Error?.Code == CommandErrorCode.Cancelled)
            throw new OperationCanceledException(result.Error.Message, cancellationToken);
        if (result.Error?.Code == CommandErrorCode.RecoveryRequired)
            throw new IsoAcquisitionRecoveryRequiredException(result.OperationId);
        if (!result.Succeeded)
            throw new IsoAcquisitionCommandException(result.Error?.Code ??
                CommandErrorCode.InternalFailure, result.OperationId, result.Error?.Message ??
                "The CLI image acquisition failed without a result.", result.Error?.DetailCode);
        var acquired = result.Data?.Deserialize<IsoAcquiredV2>(CommandProtocol.JsonOptions)
            ?? throw new InvalidDataException("The CLI image result is missing.");
        return ValidateAcquired(distro, acquired);
    }

    internal static IsoAcquisitionResult ValidateAcquired(DistroManifest distro,
        IsoAcquiredV2 acquired)
    {
        ArgumentNullException.ThrowIfNull(distro);
        ArgumentNullException.ThrowIfNull(acquired);
        if (!string.Equals(acquired.DistroId, distro.Id, StringComparison.Ordinal) ||
            !acquired.Sha256Verified || string.IsNullOrWhiteSpace(acquired.LocalPath) ||
            !Path.IsPathFullyQualified(acquired.LocalPath) ||
            acquired.VerifiedSha256 is not { Length: 64 } ||
            !acquired.VerifiedSha256.All(Uri.IsHexDigit) || acquired.SizeBytes <= 0)
            throw new InvalidDataException("The CLI image result has invalid identity.");
        if ((distro.Iso.GpgSignatureUrl is not null || distro.Iso.GpgSignedDataUrl is not null ||
             distro.Iso.GpgKeyUrl is not null || !string.IsNullOrWhiteSpace(distro.Iso.GpgKeyFile) ||
             !string.IsNullOrWhiteSpace(distro.Iso.GpgKeyFingerprint)) && !acquired.GpgVerified)
            throw new InvalidDataException("The CLI image lacks required signature verification.");
        if (string.IsNullOrWhiteSpace(distro.Iso.Sha256))
        {
            if (!acquired.GpgVerified)
                throw new InvalidDataException("The dynamic image hash lacks a verified signature.");
        }
        else if (!string.Equals(acquired.VerifiedSha256, distro.Iso.Sha256,
                     StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The CLI image differs from the selected distribution pin.");
        var file = new FileInfo(acquired.LocalPath);
        if (!file.Exists || file.Length != acquired.SizeBytes)
            throw new InvalidDataException("The verified CLI image is no longer available.");
        return new IsoAcquisitionResult(file.FullName, true, acquired.GpgVerified,
            acquired.SizeBytes, acquired.VerifiedSha256);
    }

    public async Task<Guid?> FindUnresolvedOperationAsync(string distroId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        var result = await client.ExecuteAsync(CliCommandClient.Request("iso.operations",
                new DistroSelection(distroId)), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Data is null)
            throw new InvalidOperationException("The local image operation history is unavailable.");
        IsoOperations? operations;
        try
        {
            operations = result.Data.Value.Deserialize<IsoOperations>(CommandProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local image operation history is invalid.", ex);
        }
        return FindUnresolvedOperation(operations, distroId);
    }

    internal static Guid? FindUnresolvedOperation(IsoOperations? operations, string distroId)
    {
        if (operations is null || operations.Operations is null ||
            !string.Equals(operations.DistroId, distroId, StringComparison.Ordinal))
            throw new InvalidDataException("The local image operation history has invalid identity.");
        foreach (var status in operations.Operations)
        {
            if (status is null || status.OperationId == Guid.Empty ||
                status.CapabilityId is not ("iso.acquire" or "artifact.acquire"))
                throw new InvalidDataException("The local image operation history has invalid identity.");
            if (status.State is not ("Completed" or "Failed" or "Cancelled"))
                return status.OperationId;
        }
        return null;
    }
}

internal sealed class IsoAcquisitionCommandException : InvalidOperationException
{
    public IsoAcquisitionCommandException() { }
    public IsoAcquisitionCommandException(string message) : base(message) { }
    public IsoAcquisitionCommandException(string message, Exception innerException)
        : base(message, innerException) { }

    public IsoAcquisitionCommandException(CommandErrorCode code, Guid? operationId,
        string message, string? detailCode = null) : base(message)
    {
        Code = code;
        OperationId = operationId;
        DetailCode = detailCode;
    }

    public CommandErrorCode Code { get; } = CommandErrorCode.InternalFailure;
    public Guid? OperationId { get; }
    public string? DetailCode { get; }
}

internal interface IIsoOperationStatusWorkflow
{
    Task<Guid?> FindUnresolvedOperationAsync(string distroId,
        CancellationToken cancellationToken = default);
}

internal sealed class IsoAcquisitionRecoveryRequiredException : Exception
{
    private const string DefaultMessage =
        "The local image operation needs a status check before another transfer.";

    public IsoAcquisitionRecoveryRequiredException() : base(DefaultMessage) { }
    public IsoAcquisitionRecoveryRequiredException(string message) : base(message) { }
    public IsoAcquisitionRecoveryRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
    public IsoAcquisitionRecoveryRequiredException(Guid? operationId) : base(DefaultMessage) =>
        OperationId = operationId;

    public Guid? OperationId { get; }
}
