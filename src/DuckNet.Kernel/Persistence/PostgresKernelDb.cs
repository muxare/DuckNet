using Npgsql;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// PostgreSQL stand-in for <see cref="KernelDb"/>. Local Aspire still opens
/// SQLite via <see cref="KernelDb.Open"/>. Selected when
/// <c>DUCKNET_POSTGRES_CONNECTION</c> is set (12c).
/// </summary>
public sealed class PostgresKernelDb : IDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PostgresKernelDb(NpgsqlConnection connection, string dataSource)
    {
        _connection = connection;
        DataSource = dataSource;
    }

    public string DataSource { get; }

    public static PostgresKernelDb Open(string connectionString, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureSchema(connection, schema);
        return new PostgresKernelDb(connection, connection.Database);
    }

    public T Write<T>(Func<NpgsqlConnection, NpgsqlTransaction, T> work)
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
                catch (Exception)
                {
                    // Already rolled back.
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Write(Action<NpgsqlConnection, NpgsqlTransaction> work) =>
        Write<object?>((conn, tx) =>
        {
            work(conn, tx);
            return null;
        });

    public T Read<T>(Func<NpgsqlConnection, T> work)
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
            cmd.CommandText = """
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                ORDER BY table_name
                """;
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

    private static void EnsureSchema(NpgsqlConnection connection, string schema)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = schema;
        cmd.ExecuteNonQuery();
    }
}
