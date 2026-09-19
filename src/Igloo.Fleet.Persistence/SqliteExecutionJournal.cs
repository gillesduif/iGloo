using System.Text.Json;
using Igloo.Fleet.Domain;
using Microsoft.Data.Sqlite;

namespace Igloo.Fleet.Persistence;

public sealed class SqliteExecutionJournal<T> : IExecutionJournal<T>
{
    private readonly string _path;
    public SqliteExecutionJournal(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var guard = Acquire();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_events (operation TEXT NOT NULL, sequence INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(operation, sequence));
            CREATE TRIGGER IF NOT EXISTS execution_no_update BEFORE UPDATE ON execution_events BEGIN SELECT RAISE(ABORT,'Execution events are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS execution_no_delete BEFORE DELETE ON execution_events BEGIN SELECT RAISE(ABORT,'Execution events are immutable'); END;
            """;
        command.ExecuteNonQuery();
    }

    public IDisposable Acquire() => new FileStream(_path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    public IReadOnlyList<ExecutionRecord<T>> Read(Guid operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM execution_events WHERE operation=$operation ORDER BY sequence";
        command.Parameters.AddWithValue("$operation", operationId.ToString());
        using var reader = command.ExecuteReader();
        var records = new List<ExecutionRecord<T>>();
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize<ExecutionRecord<T>>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid execution record.");
            if (record.Version != 1 || record.Sequence != records.Count || record.Correlation.OperationId != operationId)
                throw new InvalidDataException("Unsupported or discontinuous execution journal.");
            if (!Enum.IsDefined(record.Kind) || (records.Count == 0 ? record.Kind != ExecutionRecordKind.Intent :
                record.Kind == ExecutionRecordKind.Intent || records[0].Correlation != record.Correlation ||
                JsonSerializer.Serialize(records[0].Intent) != JsonSerializer.Serialize(record.Intent)))
                throw new InvalidDataException("Execution authority changed within the journal.");
            records.Add(record);
        }
        return records;
    }

    public void Append(ExecutionRecord<T> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var previous = Read(record.Correlation.OperationId);
        if (record.Version != 1 || !Enum.IsDefined(record.Kind) ||
            (previous.Count == 0 ? record.Kind != ExecutionRecordKind.Intent :
                record.Kind == ExecutionRecordKind.Intent || previous[0].Correlation != record.Correlation ||
                JsonSerializer.Serialize(previous[0].Intent) != JsonSerializer.Serialize(record.Intent)))
            throw new InvalidDataException("Journal intent and correlation are immutable.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO execution_events(operation,sequence,payload) SELECT $operation,$sequence,$payload WHERE $sequence=(SELECT COUNT(*) FROM execution_events WHERE operation=$operation)";
        command.Parameters.AddWithValue("$operation", record.Correlation.OperationId.ToString());
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Journal sequence conflict.");
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }
}
