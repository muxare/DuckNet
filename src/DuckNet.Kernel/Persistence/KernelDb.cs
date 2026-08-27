using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Single-file SQLite for this Center (one process until Step 4).
/// All reads and writes share one connection and are serialized.
/// </summary>
public sealed class KernelDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private KernelDb(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static KernelDb Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var builder = new SqliteConnectionStringBuilder { DataSource = path };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchema(connection);
        return new KernelDb(connection);
    }

    public static KernelDb OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        EnsureSchema(connection);
        return new KernelDb(connection);
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        _gate.Wait();
        try
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                var result = work(_connection, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch (SqliteException)
                {
                    // Already rolled back with the connection.
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work) =>
        Write<object?>((conn, tx) =>
        {
            work(conn, tx);
            return null;
        });

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        _gate.Wait();
        try
        {
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_log (
              offset INTEGER PRIMARY KEY AUTOINCREMENT,
              event_id TEXT NOT NULL UNIQUE,
              partition_key TEXT NOT NULL,
              type TEXT NOT NULL,
              version INTEGER NOT NULL,
              sequence_number INTEGER NOT NULL,
              payload_json TEXT NOT NULL,
              occurred_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS outbox (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              event_id TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              published_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS outbox_unpublished
              ON outbox (id) WHERE published_at IS NULL;

            CREATE TABLE IF NOT EXISTS consumer_offsets (
              consumer_group TEXT PRIMARY KEY,
              last_offset INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS inbox (
              consumer_group TEXT NOT NULL,
              event_id TEXT NOT NULL,
              processed_at TEXT NOT NULL,
              PRIMARY KEY (consumer_group, event_id)
            );

            CREATE TABLE IF NOT EXISTS duck_state (
              duck_id TEXT PRIMARY KEY,
              last_seq INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS squeak_counts (
              consumer_group TEXT NOT NULL,
              duck_id TEXT NOT NULL,
              count INTEGER NOT NULL,
              last_seq INTEGER NOT NULL,
              PRIMARY KEY (consumer_group, duck_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
