using System.Globalization;
using DuckNet.Contracts;
using Microsoft.Data.Sqlite;

namespace DuckNet.DashboardCenter;

public sealed class CommerceReadModel
{
    public void ApplyPredicted(SqliteConnection connection, SqliteTransaction tx, AssetHealthPredicted predicted)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO asset_health_latest (
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
        cmd.Parameters.AddWithValue("$id", predicted.AssetId);
        cmd.Parameters.AddWithValue("$s", predicted.Score);
        cmd.Parameters.AddWithValue("$p", predicted.PredictedFailureAt.ToString("O"));
        cmd.Parameters.AddWithValue("$r", predicted.RecommendedService);
        cmd.Parameters.AddWithValue("$v", predicted.VibrationMmS);
        cmd.Parameters.AddWithValue("$t", predicted.TemperatureC);
        cmd.Parameters.AddWithValue("$h", predicted.EngineHours);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void UpsertServiceCase(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string assetId,
        string state,
        int totalCents)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO service_cases_by_asset (alarm_id, asset_id, state, total_cents, updated_at)
            VALUES ($id, $a, $s, $c, $at)
            ON CONFLICT(alarm_id) DO UPDATE SET
              state = $s,
              total_cents = $c,
              updated_at = $at
            """;
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        cmd.Parameters.AddWithValue("$a", assetId);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$c", totalCents);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void SetState(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        string state)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE service_cases_by_asset
            SET state = $s, updated_at = $at
            WHERE alarm_id = $id
            """;
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void ApplyOrderConfirmed(SqliteConnection connection, SqliteTransaction tx, OrderConfirmed confirmed)
    {
        UpsertServiceCase(connection, tx, confirmed.AlarmId, confirmed.AssetId, "Confirmed", confirmed.TotalCents);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO confirmed_orders (order_id, alarm_id, asset_id, total_cents, confirmed_at)
            VALUES ($id, $a, $asset, $c, $at)
            """;
        cmd.Parameters.AddWithValue("$id", confirmed.OrderId.ToString());
        cmd.Parameters.AddWithValue("$a", confirmed.AlarmId.ToString());
        cmd.Parameters.AddWithValue("$asset", confirmed.AssetId);
        cmd.Parameters.AddWithValue("$c", confirmed.TotalCents);
        cmd.Parameters.AddWithValue("$at", confirmed.ConfirmedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Truncate(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM asset_health_latest;
            DELETE FROM service_cases_by_asset;
            DELETE FROM confirmed_orders;
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FleetHealthRow> ListFleet(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT asset_id, score, predicted_failure_at, recommended_service,
                   vibration_mm_s, temperature_c, engine_hours, scored_at
            FROM asset_health_latest
            ORDER BY score DESC, asset_id
            """;
        var rows = new List<FleetHealthRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FleetHealthRow(
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

    public IReadOnlyList<ServiceCaseProjection> ListServiceCases(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT alarm_id, asset_id, state, total_cents, updated_at
            FROM service_cases_by_asset
            ORDER BY updated_at DESC
            """;
        var rows = new List<ServiceCaseProjection>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ServiceCaseProjection(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                (int)reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    public IReadOnlyList<ConfirmedOrderRow> ListOrders(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT order_id, alarm_id, asset_id, total_cents, confirmed_at
            FROM confirmed_orders
            ORDER BY confirmed_at
            """;
        var rows = new List<ConfirmedOrderRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ConfirmedOrderRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                (int)reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return rows;
    }
}

public sealed record FleetHealthRow(
    string AssetId,
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService,
    double VibrationMmS,
    double TemperatureC,
    double EngineHours,
    DateTimeOffset ScoredAt);

public sealed record ServiceCaseProjection(
    Guid AlarmId,
    string AssetId,
    string State,
    int TotalCents,
    DateTimeOffset UpdatedAt);

public sealed record ConfirmedOrderRow(
    Guid OrderId,
    Guid AlarmId,
    string AssetId,
    int TotalCents,
    DateTimeOffset ConfirmedAt);
