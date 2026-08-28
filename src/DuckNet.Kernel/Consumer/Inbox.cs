using DuckNet.Kernel.Persistence;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Consumer-owned idempotency set. Dedup key is <c>EventId</c>, not payload.
/// In-memory when no <see cref="KernelDb"/> is passed (unit tests).
/// Demo and <c>KernelRunner</c> persist per consumer group, in the same
/// transaction as the offset write (Step 3).
/// Sequencer may drop late seq before this set is consulted.
/// </summary>
public sealed class Inbox
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _processed = [];
    private readonly KernelDb? _db;
    private readonly bool _enabled;
    private long _duplicateSkipCount;

    public Inbox(string consumerGroup, bool enabled = true, KernelDb? db = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ConsumerGroup = consumerGroup;
        _enabled = enabled;
        _db = db;
    }

    public string ConsumerGroup { get; }

    public bool Enabled => _enabled;

    public long DuplicateSkipCount => Interlocked.Read(ref _duplicateSkipCount);

    public bool ShouldHandle(Guid eventId)
    {
        if (!_enabled)
        {
            return true;
        }

        if (_db is not null)
        {
            var seen = _db.Read(conn => Contains(conn, eventId));
            if (seen)
            {
                Interlocked.Increment(ref _duplicateSkipCount);
                return false;
            }

            return true;
        }

        lock (_gate)
        {
            if (_processed.Contains(eventId))
            {
                Interlocked.Increment(ref _duplicateSkipCount);
                return false;
            }
        }

        return true;
    }

    public void MarkProcessed(Guid eventId)
    {
        if (!_enabled)
        {
            return;
        }

        if (_db is not null)
        {
            _db.Write((conn, tx) => InsertRow(conn, tx, eventId));
            return;
        }

        lock (_gate)
        {
            _processed.Add(eventId);
        }
    }

    public bool TryInsert(SqliteConnection connection, SqliteTransaction tx, Guid eventId)
    {
        if (!_enabled)
        {
            return true;
        }

        var inserted = InsertRow(connection, tx, eventId);
        if (!inserted)
        {
            Interlocked.Increment(ref _duplicateSkipCount);
        }

        return inserted;
    }

    public void Clear(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM inbox WHERE consumer_group = $g";
        cmd.Parameters.AddWithValue("$g", ConsumerGroup);
        cmd.ExecuteNonQuery();
        lock (_gate)
        {
            _processed.Clear();
        }
    }

    private bool Contains(SqliteConnection connection, Guid eventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM inbox
            WHERE consumer_group = $g AND event_id = $id
            """;
        cmd.Parameters.AddWithValue("$g", ConsumerGroup);
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        return cmd.ExecuteScalar() is not null;
    }

    private bool InsertRow(SqliteConnection connection, SqliteTransaction tx, Guid eventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO inbox (consumer_group, event_id, processed_at)
            VALUES ($g, $id, $at)
            """;
        cmd.Parameters.AddWithValue("$g", ConsumerGroup);
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }
}
