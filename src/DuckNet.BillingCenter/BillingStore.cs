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
    public const string StateConfirmed = "Confirmed";
    public const string StateDeclined = "Declined";

    private readonly OutboxStore _outbox;
    private readonly int _amountCents;
    private readonly TimeSpan _timeout;
    private readonly CatalogStore _catalog;
    private readonly InventoryStore _inventory;

    public BillingStore(
        OutboxStore outbox,
        int amountCents,
        TimeSpan timeout,
        CatalogStore? catalog = null,
        InventoryStore? inventory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amountCents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        _outbox = outbox;
        _amountCents = amountCents;
        _timeout = timeout;
        _catalog = catalog ?? new CatalogStore();
        _inventory = inventory ?? new InventoryStore();
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
        DateTimeOffset now) =>
        TryReserveCore(connection, tx, envelope, raised.DuckId, now);

    public bool TryReserve(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        HealthAlertRaised raised,
        DateTimeOffset now) =>
        TryReserveCore(connection, tx, envelope, raised.AssetId, now);

    public bool TryRelease(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        AlarmResolved resolved) =>
        TryReleaseCore(connection, tx, envelope, resolved.DuckId, PartsReleased.ReasonAlarmResolved);

    public bool TryRelease(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        HealthAlertResolved resolved) =>
        TryReleaseCore(connection, tx, envelope, resolved.AssetId, PartsReleased.ReasonAlarmResolved);

    public bool TryAccept(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string? traceId)
    {
        var row = Get(connection, alarmId);
        if (row is null || row.State != StateReserved)
        {
            return false;
        }

        var fitment = _catalog.FitmentForAsset(connection, row.DuckId);
        if (fitment.Count > 0 && !HasLines(connection, tx, alarmId))
        {
            return false;
        }

        if (!TrySetState(connection, tx, alarmId, StateReserved, StateConfirmed))
        {
            return false;
        }

        var consumed = _inventory.ConsumeHolds(connection, tx, alarmId);
        var lines = consumed.Count > 0 ? consumed : ListLines(connection, alarmId);
        var total = lines.Count > 0
            ? lines.Sum(l => l.Quantity * l.UnitCents)
            : row.AmountCents;
        var confirmed = new OrderConfirmed(
            Guid.NewGuid(),
            alarmId,
            row.DuckId,
            lines,
            total,
            DateTimeOffset.UtcNow);
        _outbox.Insert(
            connection,
            tx,
            OrderConfirmedEnvelope.Create(
                confirmed,
                sequenceNumber: 3,
                causationId: alarmId.ToString(),
                traceId: traceId));
        return true;
    }

    public bool TryDecline(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string? traceId)
    {
        if (!TrySetState(connection, tx, alarmId, StateReserved, StateDeclined))
        {
            return false;
        }

        PublishPartsReleased(connection, tx, alarmId, PartsReleased.ReasonDeclined, sequenceNumber: 3, traceId);
        var fee = new FeeReleased(alarmId, FeeReleased.ReasonDeclined);
        _outbox.Insert(
            connection,
            tx,
            FeeReleasedEnvelope.Create(
                fee,
                sequenceNumber: 2,
                causationId: alarmId.ToString(),
                traceId: traceId));
        return true;
    }

    public IReadOnlyList<PartLine> ListLines(SqliteConnection connection, Guid alarmId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT sku, quantity, unit_cents
            FROM service_case_lines
            WHERE alarm_id = $id
            ORDER BY sku
            """;
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        var rows = new List<PartLine>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PartLine(reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2)));
        }

        return rows;
    }

    private bool TryReserveCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        string assetId,
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
            insert.Parameters.AddWithValue("$duck", assetId);
            insert.Parameters.AddWithValue("$state", StateReserved);
            insert.Parameters.AddWithValue("$amt", _amountCents);
            insert.Parameters.AddWithValue("$at", reservedAt.ToString("O"));
            insert.Parameters.AddWithValue("$exp", expiresAt.ToString("O"));
            if (insert.ExecuteNonQuery() == 0)
            {
                return false;
            }
        }

        var fee = new FeeReserved(alarmId, assetId, _amountCents, expiresAt);
        _outbox.Insert(
            connection,
            tx,
            FeeReservedEnvelope.Create(
                fee,
                sequenceNumber: 1,
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId));

        var lines = _catalog.FitmentForAsset(connection, assetId);
        if (lines.Count > 0 && _inventory.TryReserve(connection, tx, alarmId, lines))
        {
            InsertLines(connection, tx, alarmId, lines);
            var total = lines.Sum(l => l.Quantity * l.UnitCents);
            UpdateAmount(connection, tx, alarmId, total);
            var parts = new PartsReserved(alarmId, assetId, lines, total, expiresAt);
            _outbox.Insert(
                connection,
                tx,
                PartsReservedEnvelope.Create(
                    parts,
                    sequenceNumber: 2,
                    causationId: envelope.EventId.ToString(),
                    traceId: envelope.TraceId));
        }

        return true;
    }

    private bool TryReleaseCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        string assetId,
        string reason)
    {
        var alarmId = ResolveAlarmId(connection, tx, envelope, assetId);
        if (alarmId is null)
        {
            return false;
        }

        if (!TrySetState(connection, tx, alarmId.Value, StateReserved, StateReleased))
        {
            return false;
        }

        PublishPartsReleased(connection, tx, alarmId.Value, reason, sequenceNumber: 3, envelope.TraceId);
        var fee = new FeeReleased(alarmId.Value, reason);
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

            var traceId = DuckNetTracing.CurrentOrNewTraceParent();
            PublishPartsReleased(connection, tx, row.AlarmId, PartsReleased.ReasonTimeout, sequenceNumber: 3, traceId);
            var fee = new FeeReleased(row.AlarmId, FeeReleased.ReasonTimeout);
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

    private void PublishPartsReleased(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string reason,
        long sequenceNumber,
        string? traceId)
    {
        var holds = _inventory.ReleaseHolds(connection, tx, alarmId);
        if (holds.Count == 0 && !HasLines(connection, tx, alarmId))
        {
            return;
        }

        var released = new PartsReleased(alarmId, reason);
        _outbox.Insert(
            connection,
            tx,
            PartsReleasedEnvelope.Create(
                released,
                sequenceNumber,
                causationId: alarmId.ToString(),
                traceId: traceId));
    }

    private static void InsertLines(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        IReadOnlyList<PartLine> lines)
    {
        foreach (var line in lines)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO service_case_lines (alarm_id, sku, quantity, unit_cents)
                VALUES ($id, $sku, $q, $c)
                """;
            cmd.Parameters.AddWithValue("$id", alarmId.ToString());
            cmd.Parameters.AddWithValue("$sku", line.Sku);
            cmd.Parameters.AddWithValue("$q", line.Quantity);
            cmd.Parameters.AddWithValue("$c", line.UnitCents);
            cmd.ExecuteNonQuery();
        }
    }

    private static void UpdateAmount(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        int amountCents)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE billing_sagas SET amount_cents = $amt WHERE alarm_id = $id";
        cmd.Parameters.AddWithValue("$amt", amountCents);
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        cmd.ExecuteNonQuery();
    }

    private static bool HasLines(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM service_case_lines WHERE alarm_id = $id";
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        return (long)cmd.ExecuteScalar()! > 0;
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
