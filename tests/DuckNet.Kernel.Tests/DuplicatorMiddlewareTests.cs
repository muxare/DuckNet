using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class DuplicatorMiddlewareTests
{
    [Fact]
    public async Task Rate_one_redelivers_same_event_id_and_handler_runs_once()
    {
        var inner = new InMemoryEventBus();
        var bus = new DuplicatorMiddleware(inner, duplicateRate: 1.0, seed: 1);
        var inbox = new Inbox("test");
        var counter = new SqueakCounter(bus, "test", inbox);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await bus.PublishAsync(
            SqueakedEnvelope.Create(new Squeaked("duck-1", 1, DateTimeOffset.UtcNow)),
            cts.Token);

        await ConsumerWait.UntilAttemptsAsync(counter, expected: 2, cts.Token);

        Assert.Equal(1, counter.TotalCount);
        Assert.Equal(1, bus.DuplicateCount);
        Assert.Equal(1, counter.Sequencer!.LateDropCount);
        Assert.Equal(0, counter.OutOfOrderCount);
    }

    [Fact]
    public async Task Ten_thousand_events_at_20_percent_dup_yield_exact_count()
    {
        const int unique = 10_000;
        var inner = new InMemoryEventBus();
        var bus = new DuplicatorMiddleware(inner, duplicateRate: 0.20, seed: 7);
        var inbox = new Inbox("test");
        var counter = new SqueakCounter(bus, "test", inbox);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = counter.RunAsync(cts.Token);

        var seqByDuck = new Dictionary<string, long>();
        for (var i = 0; i < unique; i++)
        {
            var duckId = $"duck-{(i % 5) + 1}";
            seqByDuck[duckId] = seqByDuck.GetValueOrDefault(duckId) + 1;
            await bus.PublishAsync(
                SqueakedEnvelope.Create(new Squeaked(duckId, seqByDuck[duckId], DateTimeOffset.UtcNow)),
                cts.Token);
        }

        await ConsumerWait.UntilAttemptsAsync(counter, expected: unique + bus.DuplicateCount, cts.Token);

        Assert.True(bus.DuplicateCount > 0);
        Assert.Equal(unique, counter.TotalCount);
        Assert.Equal(0, counter.OutOfOrderCount);
        Assert.Equal(unique, counter.CountsByDuck.Values.Sum());
    }

    [Fact]
    public async Task Disabled_inbox_counts_duplicates()
    {
        var inner = new InMemoryEventBus();
        var bus = new DuplicatorMiddleware(inner, duplicateRate: 1.0, seed: 1);
        var inbox = new Inbox("test", enabled: false);
        var counter = new SqueakCounter(bus, "test", inbox, sequencerEnabled: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await bus.PublishAsync(
            SqueakedEnvelope.Create(new Squeaked("duck-1", 1, DateTimeOffset.UtcNow)),
            cts.Token);

        await ConsumerWait.UntilAttemptsAsync(counter, expected: 2, cts.Token);

        Assert.Equal(2, counter.TotalCount);
        Assert.Equal(0, inbox.DuplicateSkipCount);
    }
}
