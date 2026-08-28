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

    public void ApplySqueak(
        SqliteConnection connection,
        SqliteTransaction tx,
        string duckId,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(duckId);

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO squeaks_by_duck_hour (duck_id, hour_utc, count)
            VALUES ($d, $h, 1)
            ON CONFLICT(duck_id, hour_utc) DO UPDATE SET count = count + 1
            """;
        cmd.Parameters.AddWithValue("$d", duckId);
        cmd.Parameters.AddWithValue("$h", HourUtc(occurredAt));
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
              SELECT duck_id, hour_utc, count
              FROM squeaks_by_duck_hour
              ORDER BY duck_id, hour_utc
              """
            : """
              SELECT duck_id, hour_utc, count
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
                reader.GetInt64(2)));
        }

        return rows;
    }

    public long TotalCount(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(count), 0) FROM squeaks_by_duck_hour";
        return (long)cmd.ExecuteScalar()!;
    }
}

public sealed record SqueakHourRow(string DuckId, string HourUtc, long Count);
