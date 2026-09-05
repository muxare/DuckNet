using DuckNet.Contracts;
using DuckNet.EventBus;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Persistence;

public sealed class EventLogStore
{
    private readonly LogExportSink? _export;

    public EventLogStore(int partitionCount = 1, LogExportSink? export = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        PartitionCount = partitionCount;
        _export = export;
    }

    public int PartitionCount { get; }

    /// <summary>
    /// Step 8 DBs have no trace columns. OrePart DBs may lack <c>log_partition</c>.
    /// Column adds run before the partition index — <c>CREATE INDEX</c> in the
    /// schema string would fail on a pre-OrePart <c>event_log</c>.
    /// No-op when this Center does not own <c>event_log</c>.
    /// </summary>
    public static void EnsureTraceColumns(SqliteConnection connection)
    {
        if (TableExists(connection, "billing_sagas"))
        {
            AddColumnIfMissing(connection, "billing_sagas", "order_id", "TEXT");
        }

        if (TableExists(connection, "equipment_assets"))
        {
            AddColumnIfMissing(
                connection,
                "equipment_assets",
                "tenant_id",
                "TEXT NOT NULL DEFAULT 'tenant-default'");
        }

        if (!TableExists(connection, "event_log"))
        {
            return;
        }

        AddColumnIfMissing(connection, "event_log", "trace_id", "TEXT");
        AddColumnIfMissing(connection, "event_log", "causation_id", "TEXT");
        AddColumnIfMissing(connection, "event_log", "log_partition", "INTEGER NOT NULL DEFAULT 0");
        EnsurePartitionIndex(connection);
    }

    public int PartitionOf(string partitionKey) =>
        DuckNet.Kernel.Consumer.PartitionShard.Assign(partitionKey, PartitionCount);

    public long Append(SqliteConnection connection, SqliteTransaction tx, EventEnvelope envelope)
    {
        var partition = PartitionOf(envelope.PartitionKey);
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO event_log
              (event_id, partition_key, type, version, sequence_number, payload_json, occurred_at, trace_id, causation_id, log_partition)
            VALUES ($id, $key, $type, $ver, $seq, $payload, $at, $trace, $causation, $part)
            """;
        insert.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        insert.Parameters.AddWithValue("$key", envelope.PartitionKey);
        insert.Parameters.AddWithValue("$type", envelope.Type);
        insert.Parameters.AddWithValue("$ver", envelope.Version);
        insert.Parameters.AddWithValue("$seq", envelope.SequenceNumber);
        insert.Parameters.AddWithValue("$payload", envelope.PayloadJson);
        insert.Parameters.AddWithValue("$at", envelope.OccurredAt.ToString("O"));
        insert.Parameters.AddWithValue("$trace", (object?)envelope.TraceId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$causation", (object?)envelope.CausationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$part", partition);
        insert.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT offset FROM event_log WHERE event_id = $id";
        select.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        var offset = (long)select.ExecuteScalar()!;
        var stamped = envelope with { LogOffset = offset };
        _export?.Append(stamped);
        return offset;
    }

    public IReadOnlyList<long> AppendBatch(
        SqliteConnection connection,
        SqliteTransaction tx,
        IReadOnlyList<EventEnvelope> envelopes)
    {
        var offsets = new List<long>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            offsets.Add(Append(connection, tx, envelope));
        }

        return offsets;
    }

    public IReadOnlyList<EventEnvelope> ReadAfter(
        SqliteConnection connection,
        long offset,
        int limit,
        int? partition = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = partition is null
            ? """
              SELECT offset, event_id, partition_key, type, version, sequence_number, payload_json, occurred_at, trace_id, causation_id
              FROM event_log
              WHERE offset > $offset
              ORDER BY offset
              LIMIT $limit
              """
            : """
              SELECT offset, event_id, partition_key, type, version, sequence_number, payload_json, occurred_at, trace_id, causation_id
              FROM event_log
              WHERE offset > $offset AND log_partition = $part
              ORDER BY offset
              LIMIT $limit
              """;
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$limit", limit);
        if (partition is not null)
        {
            cmd.Parameters.AddWithValue("$part", partition.Value);
        }

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
                TraceId: ReadNullableString(reader, 8),
                CausationId: ReadNullableString(reader, 9),
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

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Skip the index on already-huge logs so Aspire Open does not stall
    /// building a 3GB index. Partition filters still work via table scan.
    /// </summary>
    private static void EnsurePartitionIndex(SqliteConnection connection)
    {
        using (var pages = connection.CreateCommand())
        {
            pages.CommandText = "PRAGMA page_count";
            var pageCount = Convert.ToInt64(pages.ExecuteScalar() ?? 0L);
            if (pageCount > 32_768)
            {
                return;
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS event_log_partition_offset
              ON event_log (log_partition, offset)
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        string table,
        string column,
        string sqlType)
    {
        if (HasColumn(connection, table, column))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType}";
        cmd.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
