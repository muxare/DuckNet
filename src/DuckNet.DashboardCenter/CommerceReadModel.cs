using System.Globalization;
using DuckNet.Contracts;
using Microsoft.Data.Sqlite;

namespace DuckNet.DashboardCenter;

public sealed class CommerceReadModel
{
    public void ApplyPredicted(SqliteConnection connection, SqliteTransaction tx, AssetHealthPredicted predicted)
    {
        var live = ReadMeta(connection, tx, "health_model") ?? HealthModels.V1;
        var table = string.Equals(predicted.ModelVersion, live, StringComparison.Ordinal)
            ? "asset_health_latest"
            : "asset_health_shadow";
        UpsertHealthTable(connection, tx, table, predicted, includeModel: table == "asset_health_shadow");
    }

    public void CutoverHealth(SqliteConnection connection, SqliteTransaction tx, string toModel)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM asset_health_latest";
            clear.ExecuteNonQuery();
        }

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = tx;
            copy.CommandText = """
                INSERT INTO asset_health_latest (
                  asset_id, score, predicted_failure_at, recommended_service,
                  vibration_mm_s, temperature_c, engine_hours, scored_at)
                SELECT asset_id, score, predicted_failure_at, recommended_service,
                       vibration_mm_s, temperature_c, engine_hours, scored_at
                FROM asset_health_shadow
                WHERE model_version = $m
                """;
            copy.Parameters.AddWithValue("$m", toModel);
            copy.ExecuteNonQuery();
        }

        WriteMeta(connection, tx, "health_model", toModel);
    }

    public string LiveHealthModel(SqliteConnection connection) =>
        ReadMeta(connection, null, "health_model") ?? HealthModels.V1;

    public IReadOnlyList<FleetHealthRow> ListShadow(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT asset_id, score, predicted_failure_at, recommended_service,
                   vibration_mm_s, temperature_c, engine_hours, scored_at, model_version
            FROM asset_health_shadow
            ORDER BY asset_id
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
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetString(8)));
        }

        return rows;
    }

    public void ApplyCatalogPart(SqliteConnection connection, SqliteTransaction tx, PartCatalogPublished published)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO catalog_parts (sku, name, unit_cents)
            VALUES ($s, $n, $c)
            ON CONFLICT(sku) DO UPDATE SET name = $n, unit_cents = $c
            """;
        cmd.Parameters.AddWithValue("$s", published.Sku);
        cmd.Parameters.AddWithValue("$n", published.Name);
        cmd.Parameters.AddWithValue("$c", published.UnitCents);
        cmd.ExecuteNonQuery();
    }

    public void ApplyFitment(SqliteConnection connection, SqliteTransaction tx, FitmentChanged changed)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO catalog_fitment (equipment_model, sku, quantity)
            VALUES ($m, $s, $q)
            ON CONFLICT(equipment_model, sku) DO UPDATE SET quantity = $q
            """;
        cmd.Parameters.AddWithValue("$m", changed.EquipmentModel);
        cmd.Parameters.AddWithValue("$s", changed.Sku);
        cmd.Parameters.AddWithValue("$q", changed.Quantity);
        cmd.ExecuteNonQuery();
    }

    public void ApplyTenant(SqliteConnection connection, SqliteTransaction tx, string assetId, string tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO asset_tenants (asset_id, tenant_id)
            VALUES ($id, $t)
            ON CONFLICT(asset_id) DO UPDATE SET tenant_id = $t
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PartRowProjection> ListCatalogParts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sku, name, unit_cents FROM catalog_parts ORDER BY sku";
        var rows = new List<PartRowProjection>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PartRowProjection(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
        }

        return rows;
    }

    private static void UpsertHealthTable(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        AssetHealthPredicted predicted,
        bool includeModel)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = includeModel
            ? $"""
                INSERT INTO {table} (
                  asset_id, score, predicted_failure_at, recommended_service,
                  vibration_mm_s, temperature_c, engine_hours, scored_at, model_version)
                VALUES ($id, $s, $p, $r, $v, $t, $h, $at, $m)
                ON CONFLICT(asset_id) DO UPDATE SET
                  score = $s,
                  predicted_failure_at = $p,
                  recommended_service = $r,
                  vibration_mm_s = $v,
                  temperature_c = $t,
                  engine_hours = $h,
                  scored_at = $at,
                  model_version = $m
                """
            : $"""
                INSERT INTO {table} (
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
        if (includeModel)
        {
            cmd.Parameters.AddWithValue("$m", predicted.ModelVersion);
        }

        cmd.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection connection, SqliteTransaction? tx, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM projection_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = $v
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
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
            DELETE FROM asset_health_shadow;
            DELETE FROM service_cases_by_asset;
            DELETE FROM confirmed_orders;
            DELETE FROM catalog_parts;
            DELETE FROM catalog_fitment;
            DELETE FROM asset_tenants;
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FleetHealthRow> ListFleet(SqliteConnection connection, string? tenantId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tenantId is null
            ? """
              SELECT h.asset_id, h.score, h.predicted_failure_at, h.recommended_service,
                     h.vibration_mm_s, h.temperature_c, h.engine_hours, h.scored_at
              FROM asset_health_latest h
              ORDER BY h.score DESC, h.asset_id
              """
            : """
              SELECT h.asset_id, h.score, h.predicted_failure_at, h.recommended_service,
                     h.vibration_mm_s, h.temperature_c, h.engine_hours, h.scored_at
              FROM asset_health_latest h
              JOIN asset_tenants t ON t.asset_id = h.asset_id
              WHERE t.tenant_id = $tenant
              ORDER BY h.score DESC, h.asset_id
              """;
        if (tenantId is not null)
        {
            cmd.Parameters.AddWithValue("$tenant", tenantId);
        }
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

    public IReadOnlyList<ServiceCaseProjection> ListServiceCases(SqliteConnection connection, string? tenantId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tenantId is null
            ? """
              SELECT alarm_id, asset_id, state, total_cents, updated_at
              FROM service_cases_by_asset
              ORDER BY updated_at DESC
              """
            : """
              SELECT s.alarm_id, s.asset_id, s.state, s.total_cents, s.updated_at
              FROM service_cases_by_asset s
              JOIN asset_tenants t ON t.asset_id = s.asset_id
              WHERE t.tenant_id = $tenant
              ORDER BY s.updated_at DESC
              """;
        if (tenantId is not null)
        {
            cmd.Parameters.AddWithValue("$tenant", tenantId);
        }
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

    public IReadOnlyList<ConfirmedOrderRow> ListOrders(SqliteConnection connection, string? tenantId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tenantId is null
            ? """
              SELECT order_id, alarm_id, asset_id, total_cents, confirmed_at
              FROM confirmed_orders
              ORDER BY confirmed_at
              """
            : """
              SELECT o.order_id, o.alarm_id, o.asset_id, o.total_cents, o.confirmed_at
              FROM confirmed_orders o
              JOIN asset_tenants t ON t.asset_id = o.asset_id
              WHERE t.tenant_id = $tenant
              ORDER BY o.confirmed_at
              """;
        if (tenantId is not null)
        {
            cmd.Parameters.AddWithValue("$tenant", tenantId);
        }
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
    DateTimeOffset ScoredAt,
    string? ModelVersion = null);

public sealed record PartRowProjection(string Sku, string Name, int UnitCents);

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
