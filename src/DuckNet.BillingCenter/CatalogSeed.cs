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
            InsertAsset(connection, tx, asset.AssetId, asset.EquipmentModel);
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
        string model)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO equipment_assets (asset_id, equipment_model)
            VALUES ($id, $m)
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$m", model);
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
