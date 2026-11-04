using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web.Services;

/// <summary>Why an operator request did not succeed. Each kind needs a different operator action, so they stay distinct.</summary>
public enum OperatorFailureKind
{
    NotConfigured,
    Unreachable,
    CertificateNotTrusted,
    NotAuthorized,
    Rejected,
    ServerError,
}

// Public because components take it as a parameter.
public sealed record OperatorFailure(OperatorFailureKind Kind, FleetError? Error = null, int? StatusCode = null);

internal sealed record OperatorResult(OperatorFailure? Failure)
{
    public static readonly OperatorResult Success = new((OperatorFailure?)null);

    public bool Succeeded => Failure is null;

    public static OperatorResult Failed(OperatorFailureKind kind, FleetError? error = null, int? statusCode = null) =>
        new(new OperatorFailure(kind, error, statusCode));
}

internal sealed record OperatorResult<T>(T? Value, OperatorFailure? Failure)
{
    public bool Succeeded => Failure is null;

    public static OperatorResult<T> Success(T value) => new(value, null);

    public static OperatorResult<T> Failed(OperatorFailure failure) => new(default, failure);

    public static OperatorResult<T> Failed(OperatorFailureKind kind, FleetError? error = null, int? statusCode = null) =>
        new(default, new OperatorFailure(kind, error, statusCode));
}

/// <summary>Shape of GET /v2/health on the trusted Server.</summary>
internal sealed record ServerHealth(FleetProtocolVersion Protocol, string SafetyBoundary);
