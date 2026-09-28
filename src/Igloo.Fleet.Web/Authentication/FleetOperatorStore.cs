using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace Igloo.Fleet.Web.Authentication;

internal sealed class FleetOperatorStore
{
    private const string AdministratorRole = "Administrator";

    private readonly object _syncRoot = new();
    private readonly PasswordHasher<FleetOperatorAccount> _hasher = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly string _storePath;

    public FleetOperatorStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredDirectory = configuration["FleetAuthentication:DataDirectory"];
        var dataDirectory = !string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.GetFullPath(configuredDirectory)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iGloo",
                "Fleet.Web");

        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "operators.json");
    }

    public bool HasOperators()
    {
        lock (_syncRoot)
        {
            return ReadUnlocked().Count > 0;
        }
    }

    public FleetOperatorAccount? ValidateCredentials(
        string username,
        string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        lock (_syncRoot)
        {
            var normalized = NormalizeUsername(username);
            var account = ReadUnlocked().SingleOrDefault(
                candidate => candidate.NormalizedUsername == normalized);

            if (account is null)
            {
                return null;
            }

            var result = _hasher.VerifyHashedPassword(
                account,
                account.PasswordHash,
                password);

            if (result == PasswordVerificationResult.Failed)
            {
                return null;
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                var operators = ReadUnlocked();
                var index = operators.FindIndex(
                    candidate => candidate.OperatorId == account.OperatorId);

                if (index >= 0)
                {
                    account = account with
                    {
                        PasswordHash = _hasher.HashPassword(account, password)
                    };
                    operators[index] = account;
                    WriteUnlocked(operators);
                }
            }

            return account;
        }
    }

    public FleetOperatorAccount CreateInitialAdministrator(
        string username,
        string displayName,
        string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(password);

        ValidateUsername(username);
        ValidateDisplayName(displayName);
        ValidatePassword(password);

        lock (_syncRoot)
        {
            var operators = ReadUnlocked();
            if (operators.Count != 0)
            {
                throw new InvalidOperationException(
                    "Initial Fleet administrator already exists.");
            }

            var unhashed = new FleetOperatorAccount(
                Guid.NewGuid(),
                username.Trim(),
                NormalizeUsername(username),
                displayName.Trim(),
                string.Empty,
                AdministratorRole,
                DateTimeOffset.UtcNow);

            var account = unhashed with
            {
                PasswordHash = _hasher.HashPassword(unhashed, password)
            };

            operators.Add(account);
            WriteUnlocked(operators);
            return account;
        }
    }

    private List<FleetOperatorAccount> ReadUnlocked()
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        var json = File.ReadAllText(_storePath);
        return JsonSerializer.Deserialize<List<FleetOperatorAccount>>(
                   json,
                   _jsonOptions)
               ?? [];
    }

    private void WriteUnlocked(List<FleetOperatorAccount> operators)
    {
        var temporaryPath =
            _storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(operators, _jsonOptions);
            File.WriteAllText(temporaryPath, json);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeUsername(string username)
        => username.Trim().ToUpperInvariant();

    private static void ValidateUsername(string username)
    {
        var value = username.Trim();
        if (value.Length is < 3 or > 64)
        {
            throw new ArgumentException(
                "Username must contain between 3 and 64 characters.",
                nameof(username));
        }

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-')
            {
                throw new ArgumentException(
                    "Username may only contain letters, digits, dot, underscore and hyphen.",
                    nameof(username));
            }
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        var value = displayName.Trim();
        if (value.Length is < 1 or > 100)
        {
            throw new ArgumentException(
                "Display name must contain between 1 and 100 characters.",
                nameof(displayName));
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 128)
        {
            throw new ArgumentException(
                "Password must contain between 12 and 128 characters.",
                nameof(password));
        }
    }
}
