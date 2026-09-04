using DuckNet.Contracts;
using Microsoft.Data.Sqlite;

namespace DuckNet.BillingCenter;

public sealed class InventoryStore
{
    public bool TryReserve(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId,
        IReadOnlyList<PartLine> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        foreach (var line in lines)
        {
            var (onHand, reserved) = ReadLevels(connection, tx, line.Sku);
            if (onHand - reserved < line.Quantity)
            {
                return false;
            }
        }

        foreach (var line in lines)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE inventory
                SET qty_reserved = qty_reserved + $q
                WHERE sku = $sku
                """;
            update.Parameters.AddWithValue("$q", line.Quantity);
            update.Parameters.AddWithValue("$sku", line.Sku);
            update.ExecuteNonQuery();

            using var hold = connection.CreateCommand();
            hold.Transaction = tx;
            hold.CommandText = """
                INSERT INTO inventory_holds (alarm_id, sku, quantity)
                VALUES ($id, $sku, $q)
                """;
            hold.Parameters.AddWithValue("$id", alarmId.ToString());
            hold.Parameters.AddWithValue("$sku", line.Sku);
            hold.Parameters.AddWithValue("$q", line.Quantity);
            hold.ExecuteNonQuery();
        }

        return true;
    }

    public IReadOnlyList<PartLine> ReleaseHolds(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId)
    {
        var holds = ReadHolds(connection, tx, alarmId);
        foreach (var line in holds)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE inventory
                SET qty_reserved = MAX(qty_reserved - $q, 0)
                WHERE sku = $sku
                """;
            update.Parameters.AddWithValue("$q", line.Quantity);
            update.Parameters.AddWithValue("$sku", line.Sku);
            update.ExecuteNonQuery();
        }

        DeleteHolds(connection, tx, alarmId);
        return holds;
    }

    public IReadOnlyList<PartLine> ConsumeHolds(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId)
    {
        var holds = ReadHolds(connection, tx, alarmId);
        foreach (var line in holds)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE inventory
                SET qty_on_hand = MAX(qty_on_hand - $q, 0),
                    qty_reserved = MAX(qty_reserved - $q, 0)
                WHERE sku = $sku
                """;
            update.Parameters.AddWithValue("$q", line.Quantity);
            update.Parameters.AddWithValue("$sku", line.Sku);
            update.ExecuteNonQuery();
        }

        DeleteHolds(connection, tx, alarmId);
        return holds;
    }

    public (int OnHand, int Reserved) Levels(SqliteConnection connection, string sku)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT qty_on_hand, qty_reserved FROM inventory WHERE sku = $sku";
        cmd.Parameters.AddWithValue("$sku", sku);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    private static (int OnHand, int Reserved) ReadLevels(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sku)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT qty_on_hand, qty_reserved FROM inventory WHERE sku = $sku";
        cmd.Parameters.AddWithValue("$sku", sku);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    private static IReadOnlyList<PartLine> ReadHolds(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid alarmId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT h.sku, h.quantity, COALESCE(p.unit_cents, 0)
            FROM inventory_holds h
            LEFT JOIN parts p ON p.sku = h.sku
            WHERE h.alarm_id = $id
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

    private static void DeleteHolds(SqliteConnection connection, SqliteTransaction tx, Guid alarmId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM inventory_holds WHERE alarm_id = $id";
        cmd.Parameters.AddWithValue("$id", alarmId.ToString());
        cmd.ExecuteNonQuery();
    }
}
