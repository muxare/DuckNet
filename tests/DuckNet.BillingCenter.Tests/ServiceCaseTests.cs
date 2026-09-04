using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter.Tests;

public class ServiceCaseTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    [Fact]
    public void HealthAlert_reserves_fitted_parts_and_accept_confirms_order()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.82, T0.AddHours(40), "Replace drive-axle bearing kit"),
            sequenceNumber: 1,
            eventId: alarmId);

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0));
            Assert.True(store.TryAccept(conn, tx, alarmId, traceId: "00-aa-bb-01"));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateConfirmed, store.Get(conn, alarmId)!.State);
            var lines = store.ListLines(conn, alarmId);
            Assert.Contains(lines, l => l.Sku == "BRG-797-KIT");
            var unpublished = outbox.Unpublished(conn, 10)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .ToList();
            Assert.Contains(unpublished, e => e.Type == "FeeReserved");
            Assert.Contains(unpublished, e => e.Type == "PartsReserved");
            var confirmed = unpublished.Single(e => e.Type == "OrderConfirmed");
            Assert.Equal(alarmId.ToString(), confirmed.CausationId);
            Assert.Equal("00-aa-bb-01", confirmed.TraceId);
            var payload = OrderConfirmedEnvelope.Parse(confirmed);
            Assert.Equal("TRK-001", payload.AssetId);
            Assert.Equal(alarmId, payload.AlarmId);
            var (onHand, reserved) = new InventoryStore().Levels(conn, "BRG-797-KIT");
            Assert.Equal(4, onHand);
            Assert.Equal(0, reserved);
            return 0;
        });
    }

    [Fact]
    public void Decline_releases_hold_and_does_not_confirm()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.8, T0.AddHours(40), "Replace drive-axle bearing kit"),
            1,
            alarmId);

        db.Write((conn, tx) =>
        {
            store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0);
            Assert.True(store.TryDecline(conn, tx, alarmId, traceId: null));
            Assert.False(store.TryAccept(conn, tx, alarmId, traceId: null));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateDeclined, store.Get(conn, alarmId)!.State);
            var types = outbox.Unpublished(conn, 10)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson).Type)
                .ToList();
            Assert.Contains("PartsReleased", types);
            Assert.Contains("FeeReleased", types);
            Assert.DoesNotContain("OrderConfirmed", types);
            var (onHand, reserved) = new InventoryStore().Levels(conn, "BRG-797-KIT");
            Assert.Equal(5, onHand);
            Assert.Equal(0, reserved);
            return 0;
        });
    }

    [Fact]
    public void No_stock_skips_parts_and_reject_accept()
    {
        using var db = OpenSeeded();
        db.Write((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE inventory SET qty_on_hand = 0 WHERE sku = 'BRG-797-KIT'";
            cmd.ExecuteNonQuery();
        });

        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.8, T0.AddHours(40), "Replace drive-axle bearing kit"),
            1,
            alarmId);

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0));
            Assert.False(store.TryAccept(conn, tx, alarmId, traceId: null));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateReserved, store.Get(conn, alarmId)!.State);
            var types = outbox.Unpublished(conn, 10)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson).Type)
                .ToList();
            Assert.Contains("FeeReserved", types);
            Assert.DoesNotContain("PartsReserved", types);
            Assert.Empty(store.ListLines(conn, alarmId));
            return 0;
        });
    }

    [Fact]
    public void Accept_after_timeout_is_a_no_op()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.8, T0.AddHours(40), "Replace drive-axle bearing kit"),
            1,
            alarmId);

        db.Write((conn, tx) =>
        {
            store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0);
            Assert.Equal(1, store.ExpireDue(conn, tx, T0.AddMinutes(5)));
            Assert.False(store.TryAccept(conn, tx, alarmId, traceId: null));
            Assert.False(store.TryDecline(conn, tx, alarmId, traceId: null));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateExpired, store.Get(conn, alarmId)!.State);
            Assert.DoesNotContain(
                outbox.Unpublished(conn, 20).Select(row => EnvelopeJson.Deserialize(row.PayloadJson).Type),
                t => t == "OrderConfirmed");
            return 0;
        });
    }

    [Fact]
    public void Duplicate_HealthAlertRaised_does_not_double_reserve_stock()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.8, T0.AddHours(40), "Replace drive-axle bearing kit"),
            1,
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0));
            Assert.False(store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0));
        });

        db.Read(conn =>
        {
            var (_, reserved) = new InventoryStore().Levels(conn, "BRG-797-KIT");
            Assert.Equal(1, reserved);
            Assert.Equal(1, store.CountByState(conn, BillingStore.StateReserved));
            return 0;
        });
    }

    private static KernelDb OpenSeeded()
    {
        var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        db.Write((conn, tx) => CatalogSeed.Ensure(conn, tx));
        return db;
    }
}
