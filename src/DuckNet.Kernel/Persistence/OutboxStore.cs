using DuckNet.Contracts;
using DuckNet.EventBus;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

public sealed class OutboxStore
{
    public void Insert(SqliteConnection connection, SqliteTransaction tx, EventEnvelope envelope)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO outbox (event_id, payload_json)
            VALUES ($id, $payload)
            """;
        cmd.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        cmd.Parameters.AddWithValue("$payload", EnvelopeJson.Serialize(envelope));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<OutboxRow> Unpublished(SqliteConnection connection, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, payload_json
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY id
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<OutboxRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2)));
        }

        return rows;
    }

    public void MarkPublished(
        SqliteConnection connection,
        SqliteTransaction tx,
        long id,
        DateTimeOffset publishedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE outbox SET published_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", publishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int UnpublishedCount(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE published_at IS NULL";
        return (int)(long)cmd.ExecuteScalar()!;
    }
}

public sealed record OutboxRow(long Id, Guid EventId, string PayloadJson);
