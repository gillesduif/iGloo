using System.Text.Json;

namespace Igloo.Fleet.Web.Services;

internal sealed record ConsoleAuditEntry(
    DateTimeOffset AtUtc,
    string Account,
    string Action,
    string? Target,
    string Outcome,
    string? Detail);

/// <summary>
/// Append-only record of what this console did and which console account did it.
/// The Fleet Server attributes every operator action to one shared principal; this log is
/// the console-side half of the attribution until the Server carries per-operator identity.
/// Never contains secrets: tokens are recorded by identifier only.
/// </summary>
internal sealed class ConsoleAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _syncRoot = new();
    private readonly string _path;

    public ConsoleAuditLog(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = ConsoleDataDirectory.Resolve(configuration);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "console-audit.jsonl");
    }

    public void Append(ConsoleAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
        lock (_syncRoot)
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<ConsoleAuditEntry> ReadRecent(int maximum)
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var lines = File.ReadAllLines(_path);
            var entries = new List<ConsoleAuditEntry>(Math.Min(maximum, lines.Length));
            for (var index = lines.Length - 1; index >= 0 && entries.Count < maximum; index--)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    continue;
                }

                var entry = JsonSerializer.Deserialize<ConsoleAuditEntry>(lines[index], Json);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
    }

    /// <summary>Console records for one Server-side target, so pages can show which console account acted.</summary>
    public ConsoleAuditEntry? FindByTarget(string target)
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(_path).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var entry = JsonSerializer.Deserialize<ConsoleAuditEntry>(line, Json);
                if (entry is not null && string.Equals(entry.Target, target, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }
    }
}

/// <summary>Shared location for console-owned state: operator accounts, audit log and preferences.</summary>
internal static class ConsoleDataDirectory
{
    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["FleetAuthentication:DataDirectory"];
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "iGloo",
                "Fleet.Web");
    }
}
