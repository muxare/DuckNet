using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Consumer-owned high-water mark. <c>last_offset</c> is the contiguous prefix
/// of processed log offsets — required because hostile shuffle can deliver
/// offsets out of order.
/// </summary>
public sealed class ConsumerOffsetStore
{
    private readonly HashSet<long> _pending = [];
    private long _lastOffset;

    public ConsumerOffsetStore(KernelDb db, string consumerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ConsumerGroup = consumerGroup;
        _lastOffset = db.Read(conn => Read(conn, consumerGroup));
    }

    public string ConsumerGroup { get; }

    public long LastOffset => _lastOffset;

    public long MarkProcessed(SqliteConnection connection, SqliteTransaction tx, long offset)
    {
        if (offset <= _lastOffset)
        {
            return _lastOffset;
        }

        _pending.Add(offset);
        var advanced = false;
        while (_pending.Remove(_lastOffset + 1))
        {
            _lastOffset++;
            advanced = true;
        }

        if (advanced)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO consumer_offsets (consumer_group, last_offset)
                VALUES ($g, $off)
                ON CONFLICT(consumer_group) DO UPDATE SET last_offset = $off
                """;
            cmd.Parameters.AddWithValue("$g", ConsumerGroup);
            cmd.Parameters.AddWithValue("$off", _lastOffset);
            cmd.ExecuteNonQuery();
        }

        return _lastOffset;
    }

    public long Reset(SqliteConnection connection, SqliteTransaction tx)
    {
        _pending.Clear();
        _lastOffset = 0;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM consumer_offsets WHERE consumer_group = $g";
        cmd.Parameters.AddWithValue("$g", ConsumerGroup);
        cmd.ExecuteNonQuery();
        return _lastOffset;
    }

    private static long Read(SqliteConnection connection, string consumerGroup)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_offset FROM consumer_offsets WHERE consumer_group = $g";
        cmd.Parameters.AddWithValue("$g", consumerGroup);
        var value = cmd.ExecuteScalar();
        return value is long offset ? offset : 0;
    }
}
