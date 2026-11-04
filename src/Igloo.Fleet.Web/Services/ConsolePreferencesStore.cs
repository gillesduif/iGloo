using System.Text.Json;

namespace Igloo.Fleet.Web.Services;

internal sealed record ConsolePreferences(string Theme = "system", string? Culture = null)
{
    public static readonly ConsolePreferences Default = new();
}

/// <summary>Per-console-account presentation preferences. Not security state.</summary>
internal sealed class ConsolePreferencesStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _syncRoot = new();
    private readonly string _path;

    public ConsolePreferencesStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = ConsoleDataDirectory.Resolve(configuration);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "console-preferences.json");
    }

    public ConsolePreferences Get(string accountId)
    {
        lock (_syncRoot)
        {
            return ReadUnlocked().TryGetValue(accountId, out var preferences) ? preferences : ConsolePreferences.Default;
        }
    }

    public void Set(string accountId, ConsolePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_syncRoot)
        {
            var all = ReadUnlocked();
            all[accountId] = preferences;
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private Dictionary<string, ConsolePreferences> ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, ConsolePreferences>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, ConsolePreferences>>(File.ReadAllText(_path), Json)
            ?? new Dictionary<string, ConsolePreferences>(StringComparer.Ordinal);
    }
}
