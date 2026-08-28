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

    public void Truncate(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM squeaks_by_duck_hour";
        cmd.ExecuteNonQuery();
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
