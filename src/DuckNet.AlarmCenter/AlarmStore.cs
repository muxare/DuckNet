using System.Globalization;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using Microsoft.Data.Sqlite;

namespace DuckNet.AlarmCenter;

public enum AlarmTransition
{
    None,
    Raised,
    Resolved
}

public sealed class AlarmStore
{
    private readonly OutboxStore _outbox;
    private readonly int _threshold;
    private readonly int _windowSeconds;
    private readonly string _healthModel;
    private readonly bool _shadow;

    public AlarmStore(
        OutboxStore outbox,
        int threshold,
        int windowSeconds,
        string? healthModel = null,
        bool shadow = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);
        _outbox = outbox;
        _threshold = threshold;
        _windowSeconds = windowSeconds;
        _healthModel = string.Equals(healthModel, HealthModels.V2, StringComparison.Ordinal)
            ? HealthModels.V2
            : HealthModels.V1;
        _shadow = shadow;
    }

    public int Threshold => _threshold;

    public int WindowSeconds => _windowSeconds;

    public string HealthModel => _healthModel;

    public bool Shadow => _shadow;

    /// <summary>
    /// Step 4 DBs have no last_alarm_event_id. Add it nullable; new CREATE TABLE already includes it.
    /// </summary>
    public static void EnsureLastAlarmEventIdColumn(SqliteConnection connection, SqliteTransaction tx)
    {
        if (HasColumn(connection, tx, "duck_alarm_state", "last_alarm_event_id"))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE duck_alarm_state ADD COLUMN last_alarm_event_id TEXT";
        cmd.ExecuteNonQuery();
    }

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
            ON CONFLICT(duck_id) DO UPDATE SET last_seq = MAX(last_seq, $seq)
            """;
        cmd.Parameters.AddWithValue("$id", duckId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        cmd.ExecuteNonQuery();
    }

    public AlarmTransition TryRaise(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        Squeaked squeaked)
    {
        InsertWindow(connection, tx, squeaked.DuckId, envelope.EventId, squeaked.OccurredAt);
        var windowStart = squeaked.OccurredAt - TimeSpan.FromSeconds(_windowSeconds);
        TrimWindow(connection, tx, squeaked.DuckId, windowStart);
        var count = CountWindow(connection, tx, squeaked.DuckId);
        var (active, lastAlarmSeq, lastAlarmEventId) = ReadState(connection, tx, squeaked.DuckId);

        if (count > _threshold && !active)
        {
            var rate = count * (60.0 / _windowSeconds);
            var raised = new AlarmRaised(squeaked.DuckId, rate, windowStart);
            var seq = lastAlarmSeq + 1;
            var raisedEnvelope = AlarmRaisedEnvelope.Create(
                raised,
                seq,
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId);
            InsertAlarm(connection, tx, raised, raisedEnvelope.EventId);
            WriteState(connection, tx, squeaked.DuckId, active: true, seq, raisedEnvelope.EventId.ToString());
            _outbox.Insert(connection, tx, raisedEnvelope);
            return AlarmTransition.Raised;
        }

        if (count <= _threshold && active)
        {
            PublishResolved(
                connection,
                tx,
                squeaked.DuckId,
                lastAlarmSeq,
                lastAlarmEventId,
                resolvedAt: squeaked.OccurredAt,
                traceId: envelope.TraceId);
            return AlarmTransition.Resolved;
        }

        return AlarmTransition.None;
    }

    public bool TryResolve(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        string? traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(duckId);
        var (active, lastAlarmSeq, lastAlarmEventId) = ReadState(connection, tx, duckId);
        if (!active)
        {
            return false;
        }

        PublishResolved(
            connection,
            tx,
            duckId,
            lastAlarmSeq,
            lastAlarmEventId,
            resolvedAt: DateTimeOffset.UtcNow,
            traceId: traceId);
        return true;
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

    public AlarmTransition TryScore(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        SensorReadingReported reading)
    {
        UpsertReading(connection, tx, envelope.EventId, reading);
        return ScoreStored(connection, tx, envelope, reading);
    }

    public AlarmTransition TryCorrect(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        SensorReadingCorrected corrected)
    {
        var previous = ReadReading(connection, tx, corrected.AssetId, corrected.SequenceNumber);
        if (previous is null)
        {
            return AlarmTransition.None;
        }

        var reading = new SensorReadingReported(
            corrected.AssetId,
            corrected.SequenceNumber,
            corrected.OccurredAt,
            corrected.EngineHours,
            corrected.VibrationMmS,
            corrected.TemperatureC,
            corrected.TenantId);
        UpsertReading(connection, tx, envelope.EventId, reading);
        var latestSeq = ReadLatestSequence(connection, tx, corrected.AssetId);
        if (latestSeq != corrected.SequenceNumber)
        {
            EmitPrediction(connection, tx, envelope, reading, HealthScorer.Score(reading, previous.VibrationMmS, _healthModel));
            return AlarmTransition.None;
        }

        return ScoreStored(connection, tx, envelope, reading);
    }

    private AlarmTransition ScoreStored(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        SensorReadingReported reading)
    {
        var previousVibration = ReadPreviousVibration(connection, tx, reading.AssetId, reading.SequenceNumber);
        var scored = HealthScorer.Score(reading, previousVibration, _healthModel);
        UpsertHealth(connection, tx, reading, scored);
        EmitPrediction(connection, tx, envelope, reading, scored);

        if (_shadow)
        {
            var other = _healthModel == HealthModels.V2 ? HealthModels.V1 : HealthModels.V2;
            var shadow = HealthScorer.Score(reading, previousVibration, other);
            EmitPrediction(connection, tx, envelope, reading, shadow);
        }

        var (active, lastAlarmSeq, lastAlarmEventId) = ReadState(connection, tx, reading.AssetId);
        if (scored.Score >= HealthScorer.RaiseThreshold && !active)
        {
            var raised = new HealthAlertRaised(
                reading.AssetId,
                scored.Score,
                scored.PredictedFailureAt,
                scored.RecommendedService);
            var seq = lastAlarmSeq + 1;
            var raisedEnvelope = HealthAlertRaisedEnvelope.Create(
                raised,
                seq,
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId);
            InsertAlarm(
                connection,
                tx,
                new AlarmRaised(reading.AssetId, scored.Score, reading.OccurredAt),
                raisedEnvelope.EventId);
            WriteState(connection, tx, reading.AssetId, active: true, seq, raisedEnvelope.EventId.ToString());
            _outbox.Insert(connection, tx, raisedEnvelope);
            return AlarmTransition.Raised;
        }

        if (scored.Score < HealthScorer.ResolveThreshold && active)
        {
            PublishHealthResolved(
                connection,
                tx,
                reading.AssetId,
                lastAlarmSeq,
                lastAlarmEventId,
                resolvedAt: reading.OccurredAt,
                traceId: envelope.TraceId);
            return AlarmTransition.Resolved;
        }

        return AlarmTransition.None;
    }

    private void EmitPrediction(
        SqliteConnection connection,
        SqliteTransaction tx,
        EventEnvelope envelope,
        SensorReadingReported reading,
        HealthScore scored)
    {
        var predicted = new AssetHealthPredicted(
            reading.AssetId,
            scored.Score,
            scored.PredictedFailureAt,
            scored.RecommendedService,
            reading.VibrationMmS,
            reading.TemperatureC,
            reading.EngineHours,
            scored.ModelVersion);
        _outbox.Insert(
            connection,
            tx,
            AssetHealthPredictedEnvelope.Create(
                predicted,
                sequenceNumber: Math.Max(reading.SequenceNumber, 1),
                causationId: envelope.EventId.ToString(),
                traceId: envelope.TraceId));
    }

    public IReadOnlyList<AssetHealthRow> ListHealth(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT asset_id, score, predicted_failure_at, recommended_service,
                   vibration_mm_s, temperature_c, engine_hours, scored_at
            FROM asset_health
            ORDER BY asset_id
            """;
        var rows = new List<AssetHealthRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AssetHealthRow(
                reader.GetString(0),
                reader.GetDouble(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private void PublishHealthResolved(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        long lastAlarmSeq,
        string? lastAlarmEventId,
        DateTimeOffset resolvedAt,
        string? traceId)
    {
        var alarmEventId = lastAlarmEventId ?? LatestAlarmEventId(connection, tx, assetId);
        var seq = lastAlarmSeq + 1;
        var resolved = new HealthAlertResolved(assetId, resolvedAt);
        var envelope = HealthAlertResolvedEnvelope.Create(
            resolved,
            seq,
            causationId: alarmEventId,
            traceId: traceId);
        WriteState(connection, tx, assetId, active: false, seq, alarmEventId);
        _outbox.Insert(connection, tx, envelope);
    }

    private void PublishResolved(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        long lastAlarmSeq,
        string? lastAlarmEventId,
        DateTimeOffset resolvedAt,
        string? traceId)
    {
        var alarmEventId = lastAlarmEventId ?? LatestAlarmEventId(connection, tx, duckId);
        var seq = lastAlarmSeq + 1;
        var resolved = new AlarmResolved(duckId, resolvedAt);
        var envelope = AlarmResolvedEnvelope.Create(
            resolved,
            seq,
            causationId: alarmEventId,
            traceId: traceId);
        WriteState(connection, tx, duckId, active: false, seq, alarmEventId);
        _outbox.Insert(connection, tx, envelope);
    }

    private static void UpsertReading(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid eventId,
        SensorReadingReported reading)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO asset_readings (
              asset_id, sequence_number, event_id, engine_hours, vibration_mm_s, temperature_c, occurred_at)
            VALUES ($id, $seq, $e, $h, $v, $t, $at)
            ON CONFLICT(asset_id, sequence_number) DO UPDATE SET
              event_id = $e,
              engine_hours = $h,
              vibration_mm_s = $v,
              temperature_c = $t,
              occurred_at = $at
            """;
        cmd.Parameters.AddWithValue("$id", reading.AssetId);
        cmd.Parameters.AddWithValue("$seq", reading.SequenceNumber);
        cmd.Parameters.AddWithValue("$e", eventId.ToString());
        cmd.Parameters.AddWithValue("$h", reading.EngineHours);
        cmd.Parameters.AddWithValue("$v", reading.VibrationMmS);
        cmd.Parameters.AddWithValue("$t", reading.TemperatureC);
        cmd.Parameters.AddWithValue("$at", reading.OccurredAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static SensorReadingReported? ReadReading(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT engine_hours, vibration_mm_s, temperature_c, occurred_at
            FROM asset_readings
            WHERE asset_id = $id AND sequence_number = $seq
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SensorReadingReported(
            assetId,
            sequenceNumber,
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2));
    }

    private static long ReadLatestSequence(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) FROM asset_readings WHERE asset_id = $id";
        cmd.Parameters.AddWithValue("$id", assetId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static double? ReadPreviousVibration(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT vibration_mm_s FROM asset_readings
            WHERE asset_id = $id AND sequence_number < $seq
            ORDER BY sequence_number DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static void UpsertHealth(
        SqliteConnection connection,
        SqliteTransaction tx,
        SensorReadingReported reading,
        HealthScore scored)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO asset_health (
              asset_id, score, predicted_failure_at, recommended_service,
              vibration_mm_s, temperature_c, engine_hours, scored_at)
            VALUES ($id, $s, $p, $r, $v, $t, $h, $at)
            ON CONFLICT(asset_id) DO UPDATE SET
              score = $s,
              predicted_failure_at = $p,
              recommended_service = $r,
              vibration_mm_s = $v,
              temperature_c = $t,
              engine_hours = $h,
              scored_at = $at
            """;
        cmd.Parameters.AddWithValue("$id", reading.AssetId);
        cmd.Parameters.AddWithValue("$s", scored.Score);
        cmd.Parameters.AddWithValue("$p", scored.PredictedFailureAt.ToString("O"));
        cmd.Parameters.AddWithValue("$r", scored.RecommendedService);
        cmd.Parameters.AddWithValue("$v", reading.VibrationMmS);
        cmd.Parameters.AddWithValue("$t", reading.TemperatureC);
        cmd.Parameters.AddWithValue("$h", reading.EngineHours);
        cmd.Parameters.AddWithValue("$at", reading.OccurredAt.ToString("O"));
        cmd.ExecuteNonQuery();
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
        string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*) FROM squeak_window
            WHERE duck_id = $d
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static (bool Active, long LastSeq, string? LastAlarmEventId) ReadState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT active, last_seq, last_alarm_event_id FROM duck_alarm_state WHERE duck_id = $d";
        cmd.Parameters.AddWithValue("$d", duckId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (false, 0, null);
        }

        var lastEventId = reader.IsDBNull(2) ? null : reader.GetString(2);
        return (reader.GetInt64(0) != 0, reader.GetInt64(1), lastEventId);
    }

    private static void WriteState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        bool active,
        long lastSeq,
        string? lastAlarmEventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO duck_alarm_state (duck_id, active, last_seq, last_alarm_event_id)
            VALUES ($d, $a, $seq, $e)
            ON CONFLICT(duck_id) DO UPDATE SET
              active = $a,
              last_seq = $seq,
              last_alarm_event_id = $e
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
        cmd.Parameters.AddWithValue("$seq", lastSeq);
        cmd.Parameters.AddWithValue("$e", (object?)lastAlarmEventId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string? LatestAlarmEventId(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT event_id FROM alarms
            WHERE duck_id = $d
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        return cmd.ExecuteScalar() as string;
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

    private static bool HasColumn(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
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

public sealed record AlarmRow(
    string DuckId,
    double Rate,
    DateTimeOffset WindowStart,
    DateTimeOffset RaisedAt,
    Guid EventId);

public sealed record AssetHealthRow(
    string AssetId,
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService,
    double VibrationMmS,
    double TemperatureC,
    double EngineHours,
    DateTimeOffset ScoredAt);
