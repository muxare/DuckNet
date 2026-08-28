using DuckNet.Contracts;
using DuckNet.EventBus;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

/// <summary>
/// Per-consumer dead-letter rows. Inspect and replay from this Center's SQLite;
/// never a shared table and never inside the bus.
/// </summary>
public sealed class DeadLetterStore
{
    public long Insert(
        SqliteConnection connection,
        SqliteTransaction tx,
        string consumerGroup,
        EventEnvelope envelope,
        string error,
        int attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO dead_letter_queue
              (consumer_group, event_id, payload_json, error, failed_at, attempts)
            VALUES ($g, $id, $payload, $error, $at, $n)
            """;
        insert.Parameters.AddWithValue("$g", consumerGroup);
        insert.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        insert.Parameters.AddWithValue("$payload", EnvelopeJson.Serialize(envelope));
        insert.Parameters.AddWithValue("$error", error);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$n", attempts);
        insert.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT id FROM dead_letter_queue
            WHERE consumer_group = $g AND event_id = $id
            """;
        select.Parameters.AddWithValue("$g", consumerGroup);
        select.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        return (long)select.ExecuteScalar()!;
    }

    public DeadLetterRow? GetById(SqliteConnection connection, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, consumer_group, event_id, payload_json, error, failed_at, attempts
            FROM dead_letter_queue
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public IReadOnlyList<DeadLetterRow> List(SqliteConnection connection, string? consumerGroup = null)
    {
        using var cmd = connection.CreateCommand();
        if (consumerGroup is null)
        {
            cmd.CommandText = """
                SELECT id, consumer_group, event_id, payload_json, error, failed_at, attempts
                FROM dead_letter_queue
                ORDER BY id
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, consumer_group, event_id, payload_json, error, failed_at, attempts
                FROM dead_letter_queue
                WHERE consumer_group = $g
                ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$g", consumerGroup);
        }

        var rows = new List<DeadLetterRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    public long Count(SqliteConnection connection, string? consumerGroup = null)
    {
        using var cmd = connection.CreateCommand();
        if (consumerGroup is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM dead_letter_queue";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM dead_letter_queue WHERE consumer_group = $g";
            cmd.Parameters.AddWithValue("$g", consumerGroup);
        }

        return (long)cmd.ExecuteScalar()!;
    }

    public bool Delete(SqliteConnection connection, SqliteTransaction tx, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM dead_letter_queue WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public EventEnvelope EnvelopeOf(DeadLetterRow row) => EnvelopeJson.Deserialize(row.PayloadJson);

    private static DeadLetterRow ReadRow(SqliteDataReader reader) =>
        new(
            Id: reader.GetInt64(0),
            ConsumerGroup: reader.GetString(1),
            EventId: Guid.Parse(reader.GetString(2)),
            PayloadJson: reader.GetString(3),
            Error: reader.GetString(4),
            FailedAt: DateTimeOffset.Parse(
                reader.GetString(5),
                System.Globalization.CultureInfo.InvariantCulture),
            Attempts: reader.GetInt32(6));
}

public sealed record DeadLetterRow(
    long Id,
    string ConsumerGroup,
    Guid EventId,
    string PayloadJson,
    string Error,
    DateTimeOffset FailedAt,
    int Attempts);
