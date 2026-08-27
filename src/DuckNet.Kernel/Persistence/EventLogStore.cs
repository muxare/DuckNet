using DuckNet.Contracts;
using DuckNet.EventBus;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

public sealed class EventLogStore
{
    public long Append(SqliteConnection connection, SqliteTransaction tx, EventEnvelope envelope)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO event_log
              (event_id, partition_key, type, version, sequence_number, payload_json, occurred_at)
            VALUES ($id, $key, $type, $ver, $seq, $payload, $at)
            """;
        insert.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        insert.Parameters.AddWithValue("$key", envelope.PartitionKey);
        insert.Parameters.AddWithValue("$type", envelope.Type);
        insert.Parameters.AddWithValue("$ver", envelope.Version);
        insert.Parameters.AddWithValue("$seq", envelope.SequenceNumber);
        insert.Parameters.AddWithValue("$payload", envelope.PayloadJson);
        insert.Parameters.AddWithValue("$at", envelope.OccurredAt.ToString("O"));
        insert.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT offset FROM event_log WHERE event_id = $id";
        select.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        return (long)select.ExecuteScalar()!;
    }

    public IReadOnlyList<EventEnvelope> ReadAfter(SqliteConnection connection, long offset, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT offset, event_id, partition_key, type, version, sequence_number, payload_json, occurred_at
            FROM event_log
            WHERE offset > $offset
            ORDER BY offset
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<EventEnvelope>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EventEnvelope(
                EventId: Guid.Parse(reader.GetString(1)),
                Type: reader.GetString(3),
                Version: reader.GetInt32(4),
                PartitionKey: reader.GetString(2),
                SequenceNumber: reader.GetInt64(5),
                OccurredAt: DateTimeOffset.Parse(
                    reader.GetString(7),
                    System.Globalization.CultureInfo.InvariantCulture),
                PayloadJson: reader.GetString(6),
                LogOffset: reader.GetInt64(0)));
        }

        return rows;
    }

    public long Count(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_log";
        return (long)cmd.ExecuteScalar()!;
    }

    public long MaxOffset(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(offset), 0) FROM event_log";
        return (long)cmd.ExecuteScalar()!;
    }
}
