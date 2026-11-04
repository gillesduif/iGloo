namespace Igloo.Fleet.Web.Authentication;

internal sealed record ConsoleAccountInfo(string DisplayName, string Username, string Role, DateTimeOffset CreatedAtUtc);

internal sealed record FleetOperatorAccount(
    Guid OperatorId,
    string Username,
    string NormalizedUsername,
    string DisplayName,
    string PasswordHash,
    string Role,
    DateTimeOffset CreatedAtUtc);
