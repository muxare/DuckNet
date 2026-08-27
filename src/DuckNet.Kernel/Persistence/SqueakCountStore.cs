using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

public readonly record struct DuckCount(long Count, long LastSeq);

public sealed class SqueakCountStore
{
    public IReadOnlyDictionary<string, DuckCount> Load(SqliteConnection connection, string consumerGroup)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT duck_id, count, last_seq
            FROM squeak_counts
            WHERE consumer_group = $g
            """;
        cmd.Parameters.AddWithValue("$g", consumerGroup);

        var result = new Dictionary<string, DuckCount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new DuckCount(reader.GetInt64(1), reader.GetInt64(2));
        }

        return result;
    }

    public void Increment(
        SqliteConnection connection,
        SqliteTransaction tx,
        string consumerGroup,
        string duckId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO squeak_counts (consumer_group, duck_id, count, last_seq)
            VALUES ($g, $d, 1, $seq)
            ON CONFLICT(consumer_group, duck_id) DO UPDATE SET
              count = count + 1,
              last_seq = $seq
            """;
        cmd.Parameters.AddWithValue("$g", consumerGroup);
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        cmd.ExecuteNonQuery();
    }
}
