using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Single-file SQLite for one Center. Schema is per Center — never share a file.
/// All reads and writes share one connection and are serialized.
/// </summary>
public sealed class KernelDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private KernelDb(SqliteConnection connection, string dataSource)
    {
        _connection = connection;
        DataSource = dataSource;
    }

    public string DataSource { get; }

    public static KernelDb Open(string path) => Open(path, CenterSchema.Telemetry);

    public static KernelDb Open(string path, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var builder = new SqliteConnectionStringBuilder { DataSource = path };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchema(connection, schema);
        return new KernelDb(connection, path);
    }

    public static KernelDb OpenInMemory() => OpenInMemory(CenterSchema.Telemetry);

    public static KernelDb OpenInMemory(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        EnsureSchema(connection, schema);
        return new KernelDb(connection, ":memory:");
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

    public IReadOnlyList<string> TableNames() =>
        Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            var names = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    private static void EnsureSchema(SqliteConnection connection, string schema)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }
}
