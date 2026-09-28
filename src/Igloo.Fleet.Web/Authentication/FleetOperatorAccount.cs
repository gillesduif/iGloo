namespace Igloo.Fleet.Web.Authentication;

internal sealed record FleetOperatorAccount(
    Guid OperatorId,
    string Username,
    string NormalizedUsername,
    string DisplayName,
    string PasswordHash,
    string Role,
    DateTimeOffset CreatedAtUtc);