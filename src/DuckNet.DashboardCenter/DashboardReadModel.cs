using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DuckNet.DashboardCenter;

public sealed class DashboardReadModel
{
    public static string HourUtc(DateTimeOffset occurredAt)
    {
        var utc = occurredAt.ToUniversalTime();
        var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        return hour.ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Step 5 DBs have no volume_db. Add it nullable; new CREATE TABLE already includes it.
    /// </summary>
    public static void EnsureVolumeColumn(SqliteConnection connection, SqliteTransaction tx)
    {
        if (HasColumn(connection, tx, "squeaks_by_duck_hour", "volume_db"))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE squeaks_by_duck_hour ADD COLUMN volume_db REAL";
        cmd.ExecuteNonQuery();
    }

    public void ApplySqueak(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        DateTimeOffset occurredAt,
        double volumeDb = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(duckId);

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO squeaks_by_duck_hour (duck_id, hour_utc, count, volume_db)
            VALUES ($d, $h, 1, $v)
            ON CONFLICT(duck_id, hour_utc) DO UPDATE SET
              count = count + 1,
              volume_db = COALESCE(volume_db, 0) + $v
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$h", HourUtc(occurredAt));
        cmd.Parameters.AddWithValue("$v", volumeDb);
        cmd.ExecuteNonQuery();
    }

    public void ApplyReading(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid eventId,
        string assetId,
        long sequenceNumber,
        DateTimeOffset occurredAt,
        double vibrationMmS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        var hour = HourUtc(occurredAt);
        ClosePriorHours(connection, tx, assetId, hour);

        var existing = ReadFact(connection, tx, assetId, sequenceNumber);
        if (existing is not null)
        {
            AdjustHour(connection, tx, existing.AssetId, existing.HourUtc, countDelta: -1, vibrationDelta: -existing.VibrationMmS);
        }

        var closed = ReadClosedAt(connection, tx, assetId, hour);
        var reopen = closed is not null;
        UpsertHour(connection, tx, assetId, hour, vibrationMmS, reopen);
        UpsertFact(connection, tx, eventId, assetId, sequenceNumber, hour, vibrationMmS, occurredAt);
    }

    public void ApplyCorrection(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid eventId,
        string assetId,
        long sequenceNumber,
        DateTimeOffset occurredAt,
        double vibrationMmS)
    {
        ApplyReading(connection, tx, eventId, assetId, sequenceNumber, occurredAt, vibrationMmS);
    }

    public IReadOnlyList<AssetHourRow> ListReadingHours(SqliteConnection connection, string? assetId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = assetId is null
            ? """
              SELECT asset_id, hour_utc, count, vibration_sum, hour_closed_at, reopen_count
              FROM readings_by_asset_hour
              ORDER BY asset_id, hour_utc
              """
            : """
              SELECT asset_id, hour_utc, count, vibration_sum, hour_closed_at, reopen_count
              FROM readings_by_asset_hour
              WHERE asset_id = $d
              ORDER BY hour_utc
              """;
        if (assetId is not null)
        {
            cmd.Parameters.AddWithValue("$d", assetId);
        }

        var rows = new List<AssetHourRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AssetHourRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    public void Truncate(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM squeaks_by_duck_hour;
            DELETE FROM readings_by_asset_hour;
            DELETE FROM reading_facts;
            """;
        cmd.ExecuteNonQuery();
        new CommerceReadModel().Truncate(connection, tx);
    }

    public IReadOnlyList<SqueakHourRow> List(SqliteConnection connection, string? duckId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = duckId is null
            ? """
              SELECT duck_id, hour_utc, count, COALESCE(volume_db, 0)
              FROM squeaks_by_duck_hour
              ORDER BY duck_id, hour_utc
              """
            : """
              SELECT duck_id, hour_utc, count, COALESCE(volume_db, 0)
              FROM squeaks_by_duck_hour
              WHERE duck_id = $d
              ORDER BY hour_utc
              """;
        if (duckId is not null)
        {
            cmd.Parameters.AddWithValue("$d", duckId);
        }

        var rows = new List<SqueakHourRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SqueakHourRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3)));
        }

        return rows;
    }

    public long TotalCount(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(count), 0) FROM squeaks_by_duck_hour";
        return (long)cmd.ExecuteScalar()!;
    }

    public double TotalVolumeDb(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(volume_db), 0) FROM squeaks_by_duck_hour";
        return Convert.ToDouble(cmd.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ClosePriorHours(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        string currentHour)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE readings_by_asset_hour
            SET hour_closed_at = COALESCE(hour_closed_at, $at)
            WHERE asset_id = $id AND hour_utc < $h AND hour_closed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$h", currentHour);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static string? ReadClosedAt(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        string hour)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT hour_closed_at FROM readings_by_asset_hour
            WHERE asset_id = $id AND hour_utc = $h
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$h", hour);
        return cmd.ExecuteScalar() as string;
    }

    private static void UpsertHour(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        string hour,
        double vibrationMmS,
        bool reopen)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO readings_by_asset_hour (asset_id, hour_utc, count, vibration_sum, hour_closed_at, reopen_count)
            VALUES ($id, $h, 1, $v, NULL, 0)
            ON CONFLICT(asset_id, hour_utc) DO UPDATE SET
              count = count + 1,
              vibration_sum = vibration_sum + $v,
              hour_closed_at = CASE WHEN $reopen = 1 THEN NULL ELSE hour_closed_at END,
              reopen_count = reopen_count + $reopen
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$h", hour);
        cmd.Parameters.AddWithValue("$v", vibrationMmS);
        cmd.Parameters.AddWithValue("$reopen", reopen ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void AdjustHour(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        string hour,
        long countDelta,
        double vibrationDelta)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE readings_by_asset_hour
            SET count = MAX(count + $c, 0),
                vibration_sum = vibration_sum + $v
            WHERE asset_id = $id AND hour_utc = $h
            """;
        cmd.Parameters.AddWithValue("$c", countDelta);
        cmd.Parameters.AddWithValue("$v", vibrationDelta);
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$h", hour);
        cmd.ExecuteNonQuery();
    }

    private static ReadingFactRow? ReadFact(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        long sequenceNumber)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT event_id, asset_id, sequence_number, hour_utc, vibration_mm_s, occurred_at
            FROM reading_facts
            WHERE asset_id = $id AND sequence_number = $seq
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ReadingFactRow(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetDouble(4),
            reader.GetString(5));
    }

    private static void UpsertFact(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid eventId,
        string assetId,
        long sequenceNumber,
        string hour,
        double vibrationMmS,
        DateTimeOffset occurredAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO reading_facts (event_id, asset_id, sequence_number, hour_utc, vibration_mm_s, occurred_at)
            VALUES ($e, $id, $seq, $h, $v, $at)
            ON CONFLICT(asset_id, sequence_number) DO UPDATE SET
              event_id = $e,
              hour_utc = $h,
              vibration_mm_s = $v,
              occurred_at = $at
            """;
        cmd.Parameters.AddWithValue("$e", eventId.ToString());
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$seq", sequenceNumber);
        cmd.Parameters.AddWithValue("$h", hour);
        cmd.Parameters.AddWithValue("$v", vibrationMmS);
        cmd.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
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

public sealed record SqueakHourRow(string DuckId, string HourUtc, long Count, double VolumeDb);

public sealed record AssetHourRow(
    string AssetId,
    string HourUtc,
    long Count,
    double VibrationSum,
    string? HourClosedAt,
    long ReopenCount);

internal sealed record ReadingFactRow(
    Guid EventId,
    string AssetId,
    long SequenceNumber,
    string HourUtc,
    double VibrationMmS,
    string OccurredAt);
