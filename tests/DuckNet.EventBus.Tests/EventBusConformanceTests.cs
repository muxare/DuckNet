using DuckNet.Contracts;

namespace DuckNet.EventBus.Tests;

/// <summary>
/// Contract for every <see cref="IEventBus"/> implementation. Inbox — not the
/// bus — is the dedupe. Run once per adapter (in-memory, then RabbitMQ).
/// </summary>
public abstract class EventBusConformanceTests
{
    protected abstract IEventBus CreateBus();

    protected virtual Task WaitForSubscribersAsync() => Task.CompletedTask;

    protected virtual string Group(string name) => name;

    [Fact]
    public async Task Two_consumer_groups_each_receive_a_copy()
    {
        var bus = CreateBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var envelope = TestEnvelope.Squeak();

        var groupA = TakeAsync(bus.SubscribeAsync(Group("group-a"), cts.Token), 1, cts.Token);
        var groupB = TakeAsync(bus.SubscribeAsync(Group("group-b"), cts.Token), 1, cts.Token);
        await WaitForSubscribersAsync();

        await bus.PublishAsync(envelope, cts.Token);

        var a = Assert.Single(await groupA);
        var b = Assert.Single(await groupB);
        Assert.Equal(envelope.EventId, a.EventId);
        Assert.Equal(envelope.EventId, b.EventId);
    }

    [Fact]
    public async Task Duplicate_event_id_is_still_delivered()
    {
        var bus = CreateBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var envelope = TestEnvelope.Squeak(eventId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var received = TakeAsync(bus.SubscribeAsync(Group("group-a"), cts.Token), 2, cts.Token);
        await WaitForSubscribersAsync();

        await bus.PublishAsync(envelope, cts.Token);
        await bus.PublishAsync(envelope, cts.Token);

        var copies = await received;
        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy => Assert.Equal(envelope.EventId, copy.EventId));
    }

    [Fact]
    public async Task Envelope_round_trips_wire_fields()
    {
        var bus = CreateBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var envelope = TestEnvelope.Squeak();

        var received = TakeAsync(bus.SubscribeAsync(Group("group-a"), cts.Token), 1, cts.Token);
        await WaitForSubscribersAsync();

        await bus.PublishAsync(envelope, cts.Token);

        var actual = Assert.Single(await received);
        Assert.Equal(envelope.EventId, actual.EventId);
        Assert.Equal(envelope.Type, actual.Type);
        Assert.Equal(envelope.Version, actual.Version);
        Assert.Equal(envelope.PartitionKey, actual.PartitionKey);
        Assert.Equal(envelope.SequenceNumber, actual.SequenceNumber);
        Assert.Equal(envelope.OccurredAt, actual.OccurredAt);
        Assert.Equal(envelope.PayloadJson, actual.PayloadJson);
        Assert.Equal(envelope.TraceId, actual.TraceId);
        Assert.Equal(envelope.CausationId, actual.CausationId);
        Assert.Equal(envelope.LogOffset, actual.LogOffset);
    }

    public static async Task<IReadOnlyList<EventEnvelope>> TakeAsync(
        IAsyncEnumerable<EventEnvelope> source,
        int count,
        CancellationToken cancellationToken)
    {
        var items = new List<EventEnvelope>(count);
        await foreach (var envelope in source.WithCancellation(cancellationToken))
        {
            items.Add(envelope);
            if (items.Count >= count)
            {
                break;
            }
        }

        return items;
    }
}
