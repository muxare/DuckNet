using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter.Tests;

public class BillingStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

    [Fact]
    public void AlarmRaised_reserves_once_and_duplicate_pk_does_not_double_charge()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, amountCents: 100, timeout: TimeSpan.FromMinutes(5));
        var raised = new AlarmRaised("duck-1", 12, T0);
        var envelope = AlarmRaisedEnvelope.Create(raised, sequenceNumber: 1, eventId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, envelope, raised, T0));
            Assert.False(store.TryReserve(conn, tx, envelope, raised, T0));
        });

        db.Read(conn =>
        {
            var row = store.Get(conn, envelope.EventId);
            Assert.NotNull(row);
            Assert.Equal(BillingStore.StateReserved, row.State);
            Assert.Equal(100, row.AmountCents);
            Assert.Equal(T0.AddMinutes(5), row.ExpiresAt);
            Assert.Equal(1, outbox.UnpublishedCount(conn));
            var fee = EnvelopeJson.Deserialize(outbox.Unpublished(conn, 1)[0].PayloadJson);
            Assert.Equal("FeeReserved", fee.Type);
            Assert.Equal(envelope.EventId.ToString(), fee.CausationId);
            return 0;
        });
    }

    [Fact]
    public void AlarmResolved_releases_and_publishes_FeeReleased()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, amountCents: 100, timeout: TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var raisedEnv = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, T0), 1, alarmId);

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raisedEnv, AlarmRaisedEnvelope.Parse(raisedEnv), T0));
            var resolvedEnv = AlarmResolvedEnvelope.Create(
                new AlarmResolved("duck-1", T0.AddMinutes(1)),
                sequenceNumber: 2,
                causationId: alarmId.ToString());
            Assert.True(store.TryRelease(conn, tx, resolvedEnv, AlarmResolvedEnvelope.Parse(resolvedEnv)));
            Assert.False(store.TryRelease(conn, tx, resolvedEnv, AlarmResolvedEnvelope.Parse(resolvedEnv)));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateReleased, store.Get(conn, alarmId)!.State);
            var unpublished = outbox.Unpublished(conn, 10);
            Assert.Equal(2, unpublished.Count);
            var released = EnvelopeJson.Deserialize(unpublished[1].PayloadJson);
            Assert.Equal("FeeReleased", released.Type);
            var payload = FeeReleasedEnvelope.Parse(released);
            Assert.Equal(FeeReleased.ReasonAlarmResolved, payload.Reason);
            Assert.Equal(alarmId, payload.AlarmId);
            return 0;
        });
    }

    [Fact]
    public void Timeout_expires_reserved_and_is_a_no_op_after_release()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, amountCents: 100, timeout: TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var raisedEnv = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, T0), 1, alarmId);

        db.Write((conn, tx) =>
        {
            Assert.True(store.TryReserve(conn, tx, raisedEnv, AlarmRaisedEnvelope.Parse(raisedEnv), T0));
            Assert.Equal(0, store.ExpireDue(conn, tx, T0.AddMinutes(4)));
            Assert.Equal(1, store.ExpireDue(conn, tx, T0.AddMinutes(5)));
            Assert.Equal(0, store.ExpireDue(conn, tx, T0.AddMinutes(6)));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateExpired, store.Get(conn, alarmId)!.State);
            var unpublished = outbox.Unpublished(conn, 10);
            Assert.Equal(2, unpublished.Count);
            var released = FeeReleasedEnvelope.Parse(EnvelopeJson.Deserialize(unpublished[1].PayloadJson));
            Assert.Equal(FeeReleased.ReasonTimeout, released.Reason);
            return 0;
        });
    }

    [Fact]
    public void Resolve_after_timeout_does_not_release_again()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, amountCents: 100, timeout: TimeSpan.FromMinutes(5));
        var alarmId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var raisedEnv = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, T0), 1, alarmId);

        db.Write((conn, tx) =>
        {
            store.TryReserve(conn, tx, raisedEnv, AlarmRaisedEnvelope.Parse(raisedEnv), T0);
            store.ExpireDue(conn, tx, T0.AddMinutes(5));
            var resolvedEnv = AlarmResolvedEnvelope.Create(
                new AlarmResolved("duck-1", T0.AddMinutes(6)),
                sequenceNumber: 2,
                causationId: alarmId.ToString());
            Assert.False(store.TryRelease(conn, tx, resolvedEnv, AlarmResolvedEnvelope.Parse(resolvedEnv)));
        });

        db.Read(conn =>
        {
            Assert.Equal(BillingStore.StateExpired, store.Get(conn, alarmId)!.State);
            Assert.Equal(2, outbox.UnpublishedCount(conn));
            return 0;
        });
    }
}
