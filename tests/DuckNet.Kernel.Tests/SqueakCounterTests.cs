using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Producer;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class SqueakCounterTests
{
    [Fact]
    public async Task Counter_matches_published_events()
    {
        var bus = new InMemoryEventBus();
        var counter = new SqueakCounter(bus, "test");
        var simulator = new DuckSimulator(bus, duckCount: 3, seed: 7);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await simulator.PublishOneAsync("duck-1", cts.Token);
        await simulator.PublishOneAsync("duck-1", cts.Token);
        await simulator.PublishOneAsync("duck-2", cts.Token);

        await ConsumerWait.UntilCountAsync(counter, expected: 3, cts.Token);

        Assert.Equal(3, counter.TotalCount);
        Assert.Equal(0, counter.OutOfOrderCount);
        Assert.Equal(2, counter.CountsByDuck["duck-1"]);
        Assert.Equal(1, counter.CountsByDuck["duck-2"]);
    }

    [Fact]
    public async Task Duplicate_event_id_is_handled_once()
    {
        var bus = new InMemoryEventBus();
        var inbox = new Inbox("test");
        var log = new StringWriter();
        var counter = new SqueakCounter(bus, "test", inbox, logDuplicates: true, output: log);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        var eventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.UtcNow),
            eventId);

        await bus.PublishAsync(envelope, cts.Token);
        await bus.PublishAsync(envelope, cts.Token);

        await ConsumerWait.UntilAttemptsAsync(counter, expected: 2, cts.Token);

        Assert.Equal(1, counter.TotalCount);
        Assert.Equal(0, counter.OutOfOrderCount);
        Assert.Equal(1, counter.Sequencer!.LateDropCount);
        Assert.Contains(
            $"Dropping late seq 1 for duck-1 (EventId={eventId})",
            log.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_skips_duplicate_when_sequencer_is_off()
    {
        var bus = new InMemoryEventBus();
        var inbox = new Inbox("test");
        var log = new StringWriter();
        var counter = new SqueakCounter(
            bus,
            "test",
            inbox,
            logDuplicates: true,
            output: log,
            sequencerEnabled: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        var eventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.UtcNow),
            eventId);

        await bus.PublishAsync(envelope, cts.Token);
        await bus.PublishAsync(envelope, cts.Token);

        await ConsumerWait.UntilAttemptsAsync(counter, expected: 2, cts.Token);

        Assert.Equal(1, counter.TotalCount);
        Assert.Equal(1, inbox.DuplicateSkipCount);
        Assert.Contains($"Skipping duplicate {eventId}", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_of_order_feed_is_counted_in_per_key_sequence()
    {
        var bus = new InMemoryEventBus();
        var counter = new SqueakCounter(bus, "test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-B", 1, DateTimeOffset.UtcNow)), cts.Token);
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-A", 2, DateTimeOffset.UtcNow)), cts.Token);
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-A", 1, DateTimeOffset.UtcNow)), cts.Token);

        await ConsumerWait.UntilCountAsync(counter, expected: 3, cts.Token);

        Assert.Equal(3, counter.TotalCount);
        Assert.Equal(0, counter.OutOfOrderCount);
        Assert.Equal(2, counter.CountsByDuck["duck-A"]);
        Assert.Equal(1, counter.CountsByDuck["duck-B"]);
    }

    [Fact]
    public async Task Demo_runner_counts_match_published_despite_duplicates()
    {
        var result = await KernelRunner.RunDemoAsync(
            TimeSpan.FromMilliseconds(500),
            duckCount: 3,
            seed: 42,
            duplicateRate: 1.0);

        Assert.True(result.PublishedCount > 0);
        Assert.Equal(result.PublishedCount, result.TotalCount);
        Assert.Equal(result.PublishedCount, result.DuplicateDeliveries);
        Assert.Equal(0, result.OutOfOrderCount);
        Assert.Equal(result.TotalCount, result.CountsByDuck.Values.Sum());
    }

    [Fact]
    public async Task Demo_without_inbox_overcounts_when_duplicates_injected()
    {
        var result = await KernelRunner.RunDemoAsync(
            TimeSpan.FromMilliseconds(400),
            duckCount: 3,
            seed: 42,
            duplicateRate: 1.0,
            inboxEnabled: false,
            sequencerEnabled: false,
            shuffleEnabled: false);

        Assert.True(result.PublishedCount > 0);
        Assert.Equal(result.PublishedCount * 2, result.TotalCount);
        Assert.Equal(0, result.DuplicateSkips);
        Assert.Equal(0, result.SequencerLateDrops);
    }

    [Fact]
    public async Task Demo_shuffled_and_duplicated_keeps_per_key_order_and_totals()
    {
        var result = await KernelRunner.RunDemoAsync(
            TimeSpan.FromMilliseconds(600),
            duckCount: 4,
            seed: 11,
            duplicateRate: 0.20,
            shuffleEnabled: true,
            shuffleWindow: 8,
            sequencerEnabled: true);

        Assert.True(result.PublishedCount > 0);
        Assert.Equal(result.PublishedCount, result.TotalCount);
        Assert.Equal(0, result.OutOfOrderCount);
        Assert.Equal(result.TotalCount, result.CountsByDuck.Values.Sum());
    }

    [Fact]
    public void Producer_does_not_depend_on_consumer_types()
    {
        var ctor = typeof(DuckSimulator).GetConstructors().Single();
        Assert.All(ctor.GetParameters(), p =>
            Assert.DoesNotContain("Consumer", p.ParameterType.FullName ?? string.Empty));
    }
}
