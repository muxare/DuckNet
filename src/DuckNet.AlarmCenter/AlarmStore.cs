using System.Globalization;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using Microsoft.Data.Sqlite;

namespace DuckNet.AlarmCenter;

public sealed class AlarmStore
{
    private readonly OutboxStore _outbox;
    private readonly int _threshold;
    private readonly int _windowSeconds;

    public AlarmStore(OutboxStore outbox, int threshold, int windowSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);
        _outbox = outbox;
        _threshold = threshold;
        _windowSeconds = windowSeconds;
    }

    public int Threshold => _threshold;

    public int WindowSeconds => _windowSeconds;

    public IReadOnlyDictionary<string, long> LoadSqueakSeq(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT duck_id, last_seq FROM duck_progress";
        var result = new Dictionary<string, long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    public void MarkSqueakSeq(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO duck_progress (duck_id, last_seq) VALUES ($id, $seq)
            ON CONFLICT(duck_id) DO UPDATE SET last_seq = $seq
            """;
        cmd.Parameters.AddWithValue("$id", duckId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        cmd.ExecuteNonQuery();
    }

    public bool TryRaise(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        Squeaked squeaked)
    {
        InsertWindow(connection, tx, squeaked.DuckId, envelope.EventId, squeaked.OccurredAt);
        var windowStart = squeaked.OccurredAt - TimeSpan.FromSeconds(_windowSeconds);
        TrimWindow(connection, tx, squeaked.DuckId, windowStart);
        var count = CountWindow(connection, tx, squeaked.DuckId, windowStart);
        var (active, lastAlarmSeq) = ReadState(connection, tx, squeaked.DuckId);

        if (count > _threshold && !active)
        {
            var rate = count * (60.0 / _windowSeconds);
            var raised = new AlarmRaised(squeaked.DuckId, rate, windowStart);
            var seq = lastAlarmSeq + 1;
            var raisedEnvelope = AlarmRaisedEnvelope.Create(
                raised,
                seq,
                causationId: envelope.EventId.ToString());
            InsertAlarm(connection, tx, raised, raisedEnvelope.EventId);
            WriteState(connection, tx, squeaked.DuckId, active: true, seq);
            _outbox.Insert(connection, tx, raisedEnvelope);
            return true;
        }

        if (count <= _threshold && active)
        {
            WriteState(connection, tx, squeaked.DuckId, active: false, lastAlarmSeq);
        }

        return false;
    }

    public IReadOnlyList<AlarmRow> List(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT duck_id, rate, window_start, raised_at, event_id
            FROM alarms
            ORDER BY id
            """;
        var rows = new List<AlarmRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AlarmRow(
                reader.GetString(0),
                reader.GetDouble(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(4))));
        }

        return rows;
    }

    private static void InsertWindow(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        Guid eventId,
        DateTimeOffset occurredAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO squeak_window (duck_id, event_id, occurred_at)
            VALUES ($d, $e, $at)
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$e", eventId.ToString());
        cmd.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void TrimWindow(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        DateTimeOffset windowStart)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM squeak_window
            WHERE duck_id = $d AND occurred_at < $at
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$at", windowStart.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static long CountWindow(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        DateTimeOffset windowStart)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM squeak_window
            WHERE duck_id = $d AND occurred_at >= $at
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$at", windowStart.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    private static (bool Active, long LastSeq) ReadState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT active, last_seq FROM duck_alarm_state WHERE duck_id = $d";
        cmd.Parameters.AddWithValue("$d", duckId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (false, 0);
        }

        return (reader.GetInt64(0) != 0, reader.GetInt64(1));
    }

    private static void WriteState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        bool active,
        long lastSeq)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO duck_alarm_state (duck_id, active, last_seq)
            VALUES ($d, $a, $seq)
            ON CONFLICT(duck_id) DO UPDATE SET active = $a, last_seq = $seq
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
        cmd.Parameters.AddWithValue("$seq", lastSeq);
        cmd.ExecuteNonQuery();
    }

    private static void InsertAlarm(
        SqliteConnection connection,
        SqliteTransaction tx,
        AlarmRaised raised,
        Guid eventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO alarms (duck_id, rate, window_start, raised_at, event_id)
            VALUES ($d, $r, $w, $at, $e)
            """;
        cmd.Parameters.AddWithValue("$d", raised.DuckId);
        cmd.Parameters.AddWithValue("$r", raised.Rate);
        cmd.Parameters.AddWithValue("$w", raised.WindowStart.ToString("O"));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$e", eventId.ToString());
        cmd.ExecuteNonQuery();
    }
}

public sealed record AlarmRow(
    string DuckId,
    double Rate,
    DateTimeOffset WindowStart,
    DateTimeOffset RaisedAt,
    Guid EventId);
