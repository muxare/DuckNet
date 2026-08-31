using DuckNet.Contracts;
using DuckNet.EventBus;
using Npgsql;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Postgres DML for the kernel tables (event_log, inbox, outbox). Center-specific
/// stores stay SQLite-typed until 12c composition roots switch.
/// </summary>
public static class PostgresPersistence
{
    public static long AppendEventLog(NpgsqlConnection connection, NpgsqlTransaction tx, EventEnvelope envelope)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO event_log
              (event_id, partition_key, "type", version, sequence_number, payload_json, occurred_at, trace_id, causation_id)
            VALUES (@id, @key, @type, @ver, @seq, @payload, @at, @trace, @causation)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING "offset"
            """;
        insert.Parameters.AddWithValue("id", envelope.EventId.ToString());
        insert.Parameters.AddWithValue("key", envelope.PartitionKey);
        insert.Parameters.AddWithValue("type", envelope.Type);
        insert.Parameters.AddWithValue("ver", envelope.Version);
        insert.Parameters.AddWithValue("seq", envelope.SequenceNumber);
        insert.Parameters.AddWithValue("payload", envelope.PayloadJson);
        insert.Parameters.AddWithValue("at", envelope.OccurredAt.ToString("O"));
        insert.Parameters.AddWithValue("trace", (object?)envelope.TraceId ?? DBNull.Value);
        insert.Parameters.AddWithValue("causation", (object?)envelope.CausationId ?? DBNull.Value);
        var inserted = insert.ExecuteScalar();
        if (inserted is not null and not DBNull)
        {
            return Convert.ToInt64(inserted);
        }

        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """SELECT "offset" FROM event_log WHERE event_id = @id""";
        select.Parameters.AddWithValue("id", envelope.EventId.ToString());
        return (long)select.ExecuteScalar()!;
    }

    public static IReadOnlyList<EventEnvelope> ReadEventLogAfter(
        NpgsqlConnection connection,
        long offset,
        int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT "offset", event_id, partition_key, "type", version, sequence_number, payload_json, occurred_at, trace_id, causation_id
            FROM event_log
            WHERE "offset" > @offset
            ORDER BY "offset"
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("offset", offset);
        cmd.Parameters.AddWithValue("limit", limit);

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
                TraceId: reader.IsDBNull(8) ? null : reader.GetString(8),
                CausationId: reader.IsDBNull(9) ? null : reader.GetString(9),
                LogOffset: reader.GetInt64(0)));
        }

        return rows;
    }

    public static bool TryInsertInbox(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string consumerGroup,
        Guid eventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO inbox (consumer_group, event_id, processed_at)
            VALUES (@g, @id, @at)
            ON CONFLICT (consumer_group, event_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("g", consumerGroup);
        cmd.Parameters.AddWithValue("id", eventId.ToString());
        cmd.Parameters.AddWithValue("at", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public static long InsertOutbox(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        EventEnvelope envelope)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO outbox (event_id, payload_json)
            VALUES (@id, @payload)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("id", envelope.EventId.ToString());
        cmd.Parameters.AddWithValue("payload", EnvelopeJson.Serialize(envelope));
        return (long)cmd.ExecuteScalar()!;
    }

    public static void MarkOutboxPublished(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        long id,
        DateTimeOffset publishedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE outbox SET published_at = @at WHERE id = @id";
        cmd.Parameters.AddWithValue("at", publishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("id", id);
        cmd.ExecuteNonQuery();
    }

    public static int UnpublishedOutboxCount(NpgsqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE published_at IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
