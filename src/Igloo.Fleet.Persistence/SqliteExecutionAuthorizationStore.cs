using System.Text.Json;
using Igloo.Fleet.Domain;
using Microsoft.Data.Sqlite;

namespace Igloo.Fleet.Persistence;

// Provision only inside a separately verified protected state's mutable directory.
// Construction and validation never create files or migrate a database.
public sealed class SqliteExecutionAuthorizationStore
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    public SqliteExecutionAuthorizationStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _clock = clock ?? TimeProvider.System;
    }

    public void Initialize()
    {
        using (var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Flush(true);
        using var connection = Open(SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS execution_authority (
                id TEXT PRIMARY KEY, execution_id TEXT NOT NULL UNIQUE, payload TEXT NOT NULL, hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS execution_consumption (
                authorization_id TEXT PRIMARY KEY REFERENCES execution_authority(id), consumed_at TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS authority_no_update BEFORE UPDATE ON execution_authority BEGIN SELECT RAISE(ABORT,'immutable authority'); END;
            CREATE TRIGGER IF NOT EXISTS authority_no_delete BEFORE DELETE ON execution_authority BEGIN SELECT RAISE(ABORT,'immutable authority'); END;
            CREATE TRIGGER IF NOT EXISTS consumption_no_update BEFORE UPDATE ON execution_consumption BEGIN SELECT RAISE(ABORT,'immutable consumption'); END;
            CREATE TRIGGER IF NOT EXISTS consumption_no_delete BEFORE DELETE ON execution_consumption BEGIN SELECT RAISE(ABORT,'immutable consumption'); END;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public AuthorizationStatus Issue(ExecutionAuthorization authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var status = ExecutionAuthorizationRules.Validate(authority, authority.Binding, _clock.GetUtcNow());
        if (status != AuthorizationStatus.Valid) return status;
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO execution_authority(id,execution_id,payload,hash) VALUES($id,$execution,$payload,$hash)";
            command.Parameters.AddWithValue("$id", authority.AuthorizationId.ToString("D"));
            command.Parameters.AddWithValue("$execution", authority.Binding.ExecutionId.ToString("D"));
            command.Parameters.AddWithValue("$payload", System.Text.Encoding.UTF8.GetString(EvidenceIntegrity.CanonicalBytes(authority)));
            command.Parameters.AddWithValue("$hash", EvidenceIntegrity.Hash(authority));
            command.ExecuteNonQuery();
            return AuthorizationStatus.Valid;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { return AuthorizationStatus.AlreadyIssued; }
        catch (SqliteException) { return AuthorizationStatus.Unavailable; }
    }

    public AuthorizationValidation Validate(Guid id, ExecutionBinding expected)
    {
        try
        {
            using var connection = Open(SqliteOpenMode.ReadOnly);
            return Read(connection, null, id, expected);
        }
        catch (SqliteException) { return new(AuthorizationStatus.Unavailable); }
        catch (JsonException) { return new(AuthorizationStatus.Malformed); }
    }

    public AuthorizationValidation Consume(Guid id, ExecutionBinding expected)
    {
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            // Check clock and unused state after obtaining the database's write reservation.
            var validation = Read(connection, transaction, id, expected);
            if (validation.Status != AuthorizationStatus.Valid) return validation;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO execution_consumption(authorization_id,consumed_at) VALUES($id,$at)";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            transaction.Commit();
            return validation;
        }
        catch (SqliteException) { return new(AuthorizationStatus.Unavailable); }
        catch (JsonException) { return new(AuthorizationStatus.Malformed); }
    }

    private AuthorizationValidation Read(SqliteConnection connection, SqliteTransaction? transaction, Guid id, ExecutionBinding expected)
    {
        using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            return new(AuthorizationStatus.Malformed);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.payload,a.hash,a.execution_id,c.authorization_id FROM execution_authority a
            LEFT JOIN execution_consumption c ON a.id=c.authorization_id WHERE a.id=$id
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return new(AuthorizationStatus.Missing);
        var authority = JsonSerializer.Deserialize<ExecutionAuthorization>(row.GetString(0));
        if (authority is null || authority.AuthorizationId != id || !ExecutionAuthorizationRules.WellFormed(authority.Binding) ||
            authority.Binding.ExecutionId.ToString("D") != row.GetString(2) || EvidenceIntegrity.Hash(authority) != row.GetString(1))
            return new(AuthorizationStatus.Malformed);
        if (!row.IsDBNull(3)) return new(AuthorizationStatus.Consumed, authority);
        return new(ExecutionAuthorizationRules.Validate(authority, expected, _clock.GetUtcNow()), authority);
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _path, Mode = mode, Pooling = false, DefaultTimeout = 30, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }
}
