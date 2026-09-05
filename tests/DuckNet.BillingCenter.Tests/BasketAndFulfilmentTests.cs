using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter.Tests;

public class BasketAndFulfilmentTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    [Fact]
    public void Revise_basket_then_accept_uses_new_lines()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.82, T0.AddHours(40), "Replace drive-axle bearing kit"),
            1,
            alarmId);

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0));
            Assert.True(store.TryReviseBasket(
                conn,
                tx,
                alarmId,
                [new PartLine("FILTER-OIL-10", 2, 12000)],
                traceId: "00-aa-bb-01"));
            Assert.True(store.TryAccept(conn, tx, alarmId, traceId: "00-aa-bb-01"));
        });

        db.Read(conn =>
        {
            var confirmed = outbox.Unpublished(conn, 20)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .Single(e => e.Type == "OrderConfirmed");
            var payload = OrderConfirmedEnvelope.Parse(confirmed);
            Assert.Equal("FILTER-OIL-10", Assert.Single(payload.Lines).Sku);
            Assert.Equal(24000, payload.TotalCents);
            var (onHand, reserved) = new InventoryStore().Levels(conn, "BRG-797-KIT");
            Assert.Equal(5, onHand);
            Assert.Equal(0, reserved);
            var filter = new InventoryStore().Levels(conn, "FILTER-OIL-10");
            Assert.Equal(6, filter.OnHand);
            Assert.Equal(0, filter.Reserved);
            return 0;
        });
    }

    [Fact]
    public void Pick_and_ship_only_from_legal_states()
    {
        using var db = OpenSeeded();
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var raised = HealthAlertRaisedEnvelope.Create(
            new HealthAlertRaised("TRK-001", 0.8, T0.AddHours(40), "Replace"),
            1,
            alarmId);

        db.Write((conn, tx) =>
        {
            store.TryReserve(conn, tx, raised, HealthAlertRaisedEnvelope.Parse(raised), T0);
            Assert.False(store.TryPick(conn, tx, alarmId));
            Assert.True(store.TryAccept(conn, tx, alarmId, null));
            Assert.True(store.TryPick(conn, tx, alarmId));
            Assert.False(store.TryPick(conn, tx, alarmId));
            Assert.True(store.TryShip(conn, tx, alarmId, null));
            Assert.False(store.TryShip(conn, tx, alarmId, null));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateShipped, store.Get(conn, alarmId)!.State);
            Assert.Contains(
                outbox.Unpublished(conn, 20).Select(row => EnvelopeJson.Deserialize(row.PayloadJson).Type),
                t => t == "ShipmentDispatched");
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
