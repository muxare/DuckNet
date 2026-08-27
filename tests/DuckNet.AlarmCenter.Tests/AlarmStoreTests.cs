using DuckNet.AlarmCenter;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter.Tests;

public class AlarmStoreTests
{
    [Fact]
    public void Below_threshold_does_not_raise()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var store = new AlarmStore(new OutboxStore(), threshold: 3, windowSeconds: 60);
        var at = DateTimeOffset.UtcNow;

        db.Write((conn, tx) =>
        {
            for (var seq = 1; seq <= 3; seq++)
            {
                var envelope = SqueakedEnvelope.Create(new Squeaked("duck-1", seq, at.AddSeconds(seq)));
                Assert.False(store.TryRaise(conn, tx, envelope, SqueakedEnvelope.Parse(envelope)));
            }
        });

        db.Read(conn =>
        {
            Assert.Empty(store.List(conn));
            Assert.Equal(0, new OutboxStore().UnpublishedCount(conn));
            return 0;
        });
    }

    [Fact]
    public void Crossing_threshold_raises_once_then_stays_quiet()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, threshold: 3, windowSeconds: 60);
        var at = DateTimeOffset.UtcNow;

        db.Write((conn, tx) =>
        {
            for (var seq = 1; seq <= 5; seq++)
            {
                var envelope = SqueakedEnvelope.Create(new Squeaked("duck-1", seq, at.AddSeconds(seq)));
                var raised = store.TryRaise(conn, tx, envelope, SqueakedEnvelope.Parse(envelope));
                Assert.Equal(seq == 4, raised);
            }
        });

        db.Read(conn =>
        {
            var alarms = store.List(conn);
            Assert.Single(alarms);
            Assert.Equal("duck-1", alarms[0].DuckId);
            Assert.Equal(1, outbox.UnpublishedCount(conn));
            var unpublished = outbox.Unpublished(conn, 10);
            Assert.Equal("AlarmRaised", EnvelopeJson.Deserialize(unpublished[0].PayloadJson).Type);
            return 0;
        });
    }

    [Fact]
    public void Event_time_window_ignores_squeaks_outside_the_window()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var store = new AlarmStore(new OutboxStore(), threshold: 2, windowSeconds: 10);
        var t0 = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

        db.Write((conn, tx) =>
        {
            var old = SqueakedEnvelope.Create(new Squeaked("duck-1", 1, t0));
            Assert.False(store.TryRaise(conn, tx, old, SqueakedEnvelope.Parse(old)));

            var a = SqueakedEnvelope.Create(new Squeaked("duck-1", 2, t0.AddSeconds(30)));
            var b = SqueakedEnvelope.Create(new Squeaked("duck-1", 3, t0.AddSeconds(31)));
            var c = SqueakedEnvelope.Create(new Squeaked("duck-1", 4, t0.AddSeconds(32)));
            Assert.False(store.TryRaise(conn, tx, a, SqueakedEnvelope.Parse(a)));
            Assert.False(store.TryRaise(conn, tx, b, SqueakedEnvelope.Parse(b)));
            Assert.True(store.TryRaise(conn, tx, c, SqueakedEnvelope.Parse(c)));
        });
    }
}
