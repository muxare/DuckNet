using DuckNet.Contracts;
using Microsoft.Data.Sqlite;

namespace DuckNet.BillingCenter;

public sealed class CatalogStore
{
    public IReadOnlyList<PartRow> ListParts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sku, name, unit_cents FROM parts ORDER BY sku";
        var rows = new List<PartRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PartRow(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
        }

        return rows;
    }

    public string? ModelForAsset(SqliteConnection connection, string assetId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT equipment_model FROM equipment_assets WHERE asset_id = $id";
        cmd.Parameters.AddWithValue("$id", assetId);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<PartLine> FitmentForAsset(SqliteConnection connection, string assetId)
    {
        var model = ModelForAsset(connection, assetId);
        if (model is null)
        {
            return [];
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.sku, f.quantity, p.unit_cents
            FROM fitment f
            JOIN parts p ON p.sku = f.sku
            WHERE f.equipment_model = $m
            ORDER BY f.sku
            """;
        cmd.Parameters.AddWithValue("$m", model);
        var lines = new List<PartLine>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new PartLine(reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2)));
        }

        return lines;
    }
}

public sealed record PartRow(string Sku, string Name, int UnitCents);
