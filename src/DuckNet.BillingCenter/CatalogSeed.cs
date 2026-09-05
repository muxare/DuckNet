using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;
using Microsoft.Data.Sqlite;

namespace DuckNet.BillingCenter;

public static class CatalogSeed
{
    public static void Ensure(SqliteConnection connection, SqliteTransaction tx)
    {
        InsertPart(connection, tx, "BRG-797-KIT", "CAT 797 drive-axle bearing kit", 125000);
        InsertPart(connection, tx, "BRG-777-KIT", "CAT 777 wheel-bearing kit", 98000);
        InsertPart(connection, tx, "BIT-D45", "Sandvik D45 drill bit", 45000);
        InsertPart(connection, tx, "FILTER-OIL-10", "CAT 994K oil filter 10-pack", 12000);
        InsertPart(connection, tx, "BRG-HP400-KIT", "Metso HP400 crusher bearing kit", 210000);

        foreach (var asset in AssetFleetSimulator.Catalog)
        {
            InsertAsset(connection, tx, asset.AssetId, asset.EquipmentModel, OrePartTenants.Default);
        }

        InsertFitment(connection, tx, "CAT-797", "BRG-797-KIT", 1);
        InsertFitment(connection, tx, "CAT-777", "BRG-777-KIT", 1);
        InsertFitment(connection, tx, "SANDVIK-D45", "BIT-D45", 3);
        InsertFitment(connection, tx, "CAT-994K", "FILTER-OIL-10", 2);
        InsertFitment(connection, tx, "METSO-HP400", "BRG-HP400-KIT", 1);

        InsertInventory(connection, tx, "BRG-797-KIT", 5);
        InsertInventory(connection, tx, "BRG-777-KIT", 3);
        InsertInventory(connection, tx, "BIT-D45", 12);
        InsertInventory(connection, tx, "FILTER-OIL-10", 8);
        InsertInventory(connection, tx, "BRG-HP400-KIT", 2);
    }

    public static void PublishFacts(SqliteConnection connection, SqliteTransaction tx, OutboxStore outbox)
    {
        var parts = new List<PartCatalogPublished>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT sku, name, unit_cents FROM parts ORDER BY sku";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                parts.Add(new PartCatalogPublished(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
            }
        }

        var fitment = new List<FitmentChanged>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT equipment_model, sku, quantity FROM fitment ORDER BY equipment_model, sku";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                fitment.Add(new FitmentChanged(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
            }
        }

        foreach (var part in parts)
        {
            outbox.Insert(connection, tx, PartCatalogPublishedEnvelope.Create(part));
        }

        foreach (var row in fitment)
        {
            outbox.Insert(connection, tx, FitmentChangedEnvelope.Create(row));
        }
    }

    private static void InsertPart(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sku,
        string name,
        int unitCents)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO parts (sku, name, unit_cents)
            VALUES ($sku, $n, $c)
            """;
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", unitCents);
        cmd.ExecuteNonQuery();
    }

    private static void InsertAsset(
        SqliteConnection connection,
        SqliteTransaction tx,
        string assetId,
        string model,
        string tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO equipment_assets (asset_id, equipment_model, tenant_id)
            VALUES ($id, $m, $t)
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertFitment(
        SqliteConnection connection,
        SqliteTransaction tx,
        string model,
        string sku,
        int quantity)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO fitment (equipment_model, sku, quantity)
            VALUES ($m, $sku, $q)
            """;
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$q", quantity);
        cmd.ExecuteNonQuery();
    }

    private static void InsertInventory(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sku,
        int onHand)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO inventory (sku, qty_on_hand, qty_reserved)
            VALUES ($sku, $q, 0)
            """;
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$q", onHand);
        cmd.ExecuteNonQuery();
    }
}
