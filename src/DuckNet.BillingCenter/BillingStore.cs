using System.Globalization;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using Microsoft.Data.Sqlite;

namespace DuckNet.BillingCenter;

public sealed class BillingStore
{
    public const string StateReserved = "Reserved";
    public const string StateReleased = "Released";
    public const string StateExpired = "Expired";

    private readonly OutboxStore _outbox;
    private readonly int _amountCents;
    private readonly TimeSpan _timeout;

    public BillingStore(OutboxStore outbox, int amountCents, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amountCents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        _outbox = outbox;
        _amountCents = amountCents;
        _timeout = timeout;
    }

    public int AmountCents => _amountCents;

    public TimeSpan Timeout => _timeout;

    public IReadOnlyDictionary<string, long> LoadAlarmSeq(SqliteConnection connection)
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

    public void MarkAlarmSeq(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO duck_progress (duck_id, last_seq) VALUES ($id, $seq)
            ON CONFLICT(duck_id) DO UPDATE SET last_seq = MAX(last_seq, $seq)
            """;
        cmd.Parameters.AddWithValue("$id", duckId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        cmd.ExecuteNonQuery();
    }

    public bool TryReserve(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        AlarmRaised raised,
        DateTimeOffset now)
    {
        var alarmId = envelope.EventId;
        var reservedAt = now;
        var expiresAt = now + _timeout;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO billing_sagas
                  (alarm_id, duck_id, state, amount_cents, reserved_at, expires_at)
                VALUES ($id, $duck, $state, $amt, $at, $exp)
                """;
            insert.Parameters.AddWithValue("$id", alarmId.ToString());
            insert.Parameters.AddWithValue("$duck", raised.DuckId);
            insert.Parameters.AddWithValue("$state", StateReserved);
            insert.Parameters.AddWithValue("$amt", _amountCents);
            insert.Parameters.AddWithValue("$at", reservedAt.ToString("O"));
            insert.Parameters.AddWithValue("$exp", expiresAt.ToString("O"));
            if (insert.ExecuteNonQuery() == 0)
            {
                return false;
            }
        }

        var fee = new FeeReserved(alarmId, raised.DuckId, _amountCents, expiresAt);
        _outbox.Insert(
            connection,
            tx,
            FeeReservedEnvelope.Create(
                fee,
                sequenceNumber: 1,
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId));
        return true;
    }

    public bool TryRelease(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        AlarmResolved resolved)
    {
        var alarmId = ResolveAlarmId(connection, tx, envelope, resolved.DuckId);
        if (alarmId is null)
        {
            return false;
        }

        if (!TrySetState(connection, tx, alarmId.Value, StateReserved, StateReleased))
        {
            return false;
        }

        var fee = new FeeReleased(alarmId.Value, FeeReleased.ReasonAlarmResolved);
        _outbox.Insert(
            connection,
            tx,
            FeeReleasedEnvelope.Create(
                fee,
                sequenceNumber: 2,
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId));
        return true;
    }

    public int ExpireDue(SqliteConnection connection, SqliteTransaction tx, DateTimeOffset now)
    {
        var due = ReservedDue(connection, tx, now);
        var expired = 0;
        foreach (var row in due)
        {
            if (!TrySetState(connection, tx, row.AlarmId, StateReserved, StateExpired))
            {
                continue;
            }

            var fee = new FeeReleased(row.AlarmId, FeeReleased.ReasonTimeout);
            var traceId = DuckNetTracing.CurrentOrNewTraceParent();
            _outbox.Insert(
                connection,
                tx,
                FeeReleasedEnvelope.Create(
                    fee,
                    sequenceNumber: 2,
                    causationId: row.AlarmId.ToString(),
                    traceId: traceId));
            expired++;
        }

        return expired;
    }

    public IReadOnlyList<BillingSagaRow> List(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT alarm_id, duck_id, state, amount_cents, reserved_at, expires_at
            FROM billing_sagas
            ORDER BY reserved_at
            """;
        var rows = new List<BillingSagaRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    public BillingSagaRow? Get(SqliteConnection connection, Guid alarmId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT alarm_id, duck_id, state, amount_cents, reserved_at, expires_at
            FROM billing_sagas
            WHERE alarm_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public int CountByState(SqliteConnection connection, string state)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM billing_sagas WHERE state = $s";
        cmd.Parameters.AddWithValue("$s", state);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static Guid? ResolveAlarmId(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        string duckId)
    {
        if (Guid.TryParse(envelope.CausationId, out var fromCausation))
        {
            return fromCausation;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT alarm_id FROM billing_sagas
            WHERE duck_id = $d AND state = $s
            ORDER BY reserved_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$s", StateReserved);
        var value = cmd.ExecuteScalar() as string;
        return value is null ? null : Guid.Parse(value);
    }

    private static bool TrySetState(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string from,
        string to)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE billing_sagas
            SET state = $to
            WHERE alarm_id = $id AND state = $from
            """;
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        cmd.Parameters.AddWithValue("$from", from);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static IReadOnlyList<BillingSagaRow> ReservedDue(
        SqliteConnection connection,
        SqliteTransaction tx,
        DateTimeOffset now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT alarm_id, duck_id, state, amount_cents, reserved_at, expires_at
            FROM billing_sagas
            WHERE state = $s
            """;
        cmd.Parameters.AddWithValue("$s", StateReserved);
        var rows = new List<BillingSagaRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadRow(reader);
            if (row.ExpiresAt <= now)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static BillingSagaRow ReadRow(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (int)reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
}

public sealed record BillingSagaRow(
    Guid AlarmId,
    string DuckId,
    string State,
    int AmountCents,
    DateTimeOffset ReservedAt,
    DateTimeOffset ExpiresAt);
