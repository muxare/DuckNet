using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter.Tests;

public class BillingConsumerTests
{
    [Fact]
    public async Task Happy_path_reserves_then_releases()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var bus = new InMemoryEventBus();
        var consumer = CreateConsumer(bus, db, store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var raised = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, DateTimeOffset.UtcNow), 1) with { LogOffset = 1 };
        var resolved = AlarmResolvedEnvelope.Create(
            new AlarmResolved("duck-1", DateTimeOffset.UtcNow),
            sequenceNumber: 2,
            causationId: raised.EventId.ToString()) with
        { LogOffset = 2 };

        await bus.PublishAsync(raised, cts.Token);
        await WaitUntilAsync(() => consumer.ReservedCount >= 1, cts.Token);
        await bus.PublishAsync(resolved, cts.Token);
        await WaitUntilAsync(() => consumer.ReleasedCount >= 1, cts.Token);

        var row = db.Read(conn => store.Get(conn, raised.EventId));
        Assert.Equal(BillingStore.StateReleased, row!.State);
        Assert.Equal(2, db.Read(conn => outbox.UnpublishedCount(conn)));
    }

    [Fact]
    public async Task Duplicate_AlarmRaised_does_not_double_charge()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var bus = new InMemoryEventBus();
        var consumer = CreateConsumer(bus, db, store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var raised = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, DateTimeOffset.UtcNow), 1) with { LogOffset = 1 };
        await bus.PublishAsync(raised, cts.Token);
        await bus.PublishAsync(raised, cts.Token);
        await WaitUntilAsync(() => consumer.AttemptCount >= 2, cts.Token);

        Assert.Equal(1, consumer.ReservedCount);
        Assert.Equal(1, db.Read(conn => store.CountByState(conn, BillingStore.StateReserved)));
        Assert.Equal(1, db.Read(conn => outbox.UnpublishedCount(conn)));
    }

    [Fact]
    public async Task Sequencer_holds_AlarmResolved_until_AlarmRaised()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var bus = new InMemoryEventBus();
        var sequencer = new PerKeySequencer();
        var consumer = CreateConsumer(bus, db, store, sequencer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var alarmId = Guid.NewGuid();
        var resolved = AlarmResolvedEnvelope.Create(
            new AlarmResolved("duck-1", DateTimeOffset.UtcNow),
            sequenceNumber: 2,
            causationId: alarmId.ToString()) with
        { LogOffset = 2 };
        var raised = AlarmRaisedEnvelope.Create(
            new AlarmRaised("duck-1", 12, DateTimeOffset.UtcNow),
            1,
            alarmId) with
        { LogOffset = 1 };

        await bus.PublishAsync(resolved, cts.Token);
        await Task.Delay(50, cts.Token);
        Assert.Equal(0, consumer.ReservedCount);

        await bus.PublishAsync(raised, cts.Token);
        await WaitUntilAsync(() => consumer.ReleasedCount >= 1, cts.Token);

        Assert.Equal(BillingStore.StateReleased, db.Read(conn => store.Get(conn, alarmId)!.State));
    }

    [Fact]
    public async Task Timeout_worker_expires_without_resolve()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Billing);
        var outbox = new OutboxStore();
        var t0 = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var time = new MutableTimeProvider(t0);
        var store = new BillingStore(outbox, 100, TimeSpan.FromMinutes(5));
        var bus = new InMemoryEventBus();
        var consumer = CreateConsumer(bus, db, store, time: time);
        var timeout = new SagaTimeoutWorker(db, store, time, TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var raised = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, t0), 1) with { LogOffset = 1 };
        await bus.PublishAsync(raised, cts.Token);
        await WaitUntilAsync(() => consumer.ReservedCount >= 1, cts.Token);

        time.Set(t0.AddMinutes(5));
        await timeout.DrainAsync(cts.Token);

        Assert.Equal(BillingStore.StateExpired, db.Read(conn => store.Get(conn, raised.EventId)!.State));
        var unpublished = db.Read(conn => outbox.Unpublished(conn, 10));
        Assert.Equal(2, unpublished.Count);
        Assert.Equal(
            FeeReleased.ReasonTimeout,
            FeeReleasedEnvelope.Parse(EnvelopeJson.Deserialize(unpublished[1].PayloadJson)).Reason);
    }

    private static BillingConsumer CreateConsumer(
        IEventBus bus,
        KernelDb db,
        BillingStore store,
        PerKeySequencer? sequencer = null,
        TimeProvider? time = null)
    {
        return new BillingConsumer(
            bus,
            db,
            new Inbox(BillingConsumer.ConsumerGroup, enabled: true, db),
            new ConsumerOffsetStore(db, BillingConsumer.ConsumerGroup),
            store,
            sequencer,
            time: time,
            shardCount: 1);
    }

    private static async Task WaitUntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!done())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utc;

    public MutableTimeProvider(DateTimeOffset utc) => _utc = utc;

    public void Set(DateTimeOffset utc) => _utc = utc;

    public override DateTimeOffset GetUtcNow() => _utc;
}
