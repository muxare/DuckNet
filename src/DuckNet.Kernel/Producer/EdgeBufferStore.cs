using DuckNet.Contracts;
using DuckNet.EventBus;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Producer;

public sealed class EdgeBufferStore
{
    public void Insert(SqliteConnection connection, SqliteTransaction tx, EventEnvelope envelope)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO edge_buffer (event_id, payload_json)
            VALUES ($id, $payload)
            """;
        cmd.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        cmd.Parameters.AddWithValue("$payload", EnvelopeJson.Serialize(envelope));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<EdgeBufferRow> Unflushed(SqliteConnection connection, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, payload_json
            FROM edge_buffer
            WHERE flushed_at IS NULL
            ORDER BY id
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<EdgeBufferRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EdgeBufferRow(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2)));
        }

        return rows;
    }

    public void MarkFlushed(
        SqliteConnection connection,
        SqliteTransaction tx,
        long id,
        DateTimeOffset flushedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE edge_buffer SET flushed_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", flushedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int UnflushedCount(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM edge_buffer WHERE flushed_at IS NULL";
        return (int)(long)cmd.ExecuteScalar()!;
    }
}

public sealed record EdgeBufferRow(long Id, Guid EventId, string PayloadJson);
