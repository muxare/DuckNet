using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

public sealed class StateStore
{
    public long NextSequence(SqliteConnection connection, SqliteTransaction tx, string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO duck_state (duck_id, last_seq) VALUES ($id, 1)
            ON CONFLICT(duck_id) DO UPDATE SET last_seq = last_seq + 1
            RETURNING last_seq;
            """;
        cmd.Parameters.AddWithValue("$id", duckId);
        return (long)cmd.ExecuteScalar()!;
    }

    public long Get(SqliteConnection connection, string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_seq FROM duck_state WHERE duck_id = $id";
        cmd.Parameters.AddWithValue("$id", duckId);
        var value = cmd.ExecuteScalar();
        return value is long seq ? seq : 0;
    }
}
