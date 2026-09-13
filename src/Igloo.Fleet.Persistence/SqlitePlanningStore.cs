using System.Text.Json;
using Igloo.Fleet.Domain;
using Microsoft.Data.Sqlite;

namespace Igloo.Fleet.Persistence;

/// <summary>Single-node transactional aggregate storage. Schema upgrades never delete historical data.</summary>
public sealed class SqlitePlanningStore : IPlanningStore
{
    private readonly string _connectionString;
    public SqlitePlanningStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), DefaultTimeout = 30, Pooling = false,
        }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (current > 1) throw new InvalidOperationException("Fleet database schema is newer than this server.");
        if (current == 0)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE fleet_state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                INSERT INTO fleet_state(id,payload) VALUES(1,'{}');
                PRAGMA user_version=1;
                """;
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public T Read<T>(Func<PlanningState, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM fleet_state WHERE id=1";
        return read(JsonSerializer.Deserialize<PlanningState>((string)command.ExecuteScalar()!)!);
    }

    public T Transact<T>(Func<PlanningState, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var connection = Open();
        // Immediate transaction serializes claim, token consumption, evidence and approval writes across processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM fleet_state WHERE id=1";
        var state = JsonSerializer.Deserialize<PlanningState>((string)command.ExecuteScalar()!)!;
        var result = update(state);
        command.CommandText = "UPDATE fleet_state SET payload=$payload WHERE id=1";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
        transaction.Commit();
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
