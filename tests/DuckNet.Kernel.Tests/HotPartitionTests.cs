using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;

namespace DuckNet.Kernel.Tests;

public class HotPartitionTests
{
    [Fact]
    public void Assign_is_stable_and_in_range()
    {
        for (var i = 1; i <= 50; i++)
        {
            var key = $"duck-{i}";
            var shard = PartitionShard.Assign(key, 3);
            Assert.InRange(shard, 0, 2);
            Assert.Equal(shard, PartitionShard.Assign(key, 3));
        }

        Assert.Equal(0, PartitionShard.Assign("duck-1", 1));
        var on0 = PartitionShard.FirstKeyOnShard(0, 3);
        var on1 = PartitionShard.FirstKeyOnShard(1, 3);
        Assert.Equal(0, PartitionShard.Assign(on0, 3));
        Assert.Equal(1, PartitionShard.Assign(on1, 3));
        Assert.NotEqual(on0, on1);
    }

    [Fact]
    public async Task Single_shard_hot_key_starves_the_quiet_duck()
    {
        var snapshot = await RunBurstAsync(shardCount: 1);
        var quiet = snapshot.Keys.Single(k => k.PartitionKey == PartitionShard.FirstKeyOnShard(1, 3));
        Assert.True(quiet.MaxLagMs >= 150, $"quiet lag {quiet.MaxLagMs}ms should wait behind the hot burst");
    }

    [Fact]
    public async Task Sharded_workers_keep_the_quiet_duck_near_realtime()
    {
        var starved = await RunBurstAsync(shardCount: 1);
        var sharded = await RunBurstAsync(shardCount: 3);

        var starvedQuiet = starved.Keys.Single(k => k.PartitionKey == PartitionShard.FirstKeyOnShard(1, 3));
        var shardedQuiet = sharded.Keys.Single(k => k.PartitionKey == PartitionShard.FirstKeyOnShard(1, 3));
        var shardedHot = sharded.Keys.Single(k => k.PartitionKey == PartitionShard.FirstKeyOnShard(0, 3));

        Assert.True(
            shardedQuiet.MaxLagMs < starvedQuiet.MaxLagMs / 2,
            $"sharded quiet {shardedQuiet.MaxLagMs}ms vs starved {starvedQuiet.MaxLagMs}ms");
        Assert.True(
            shardedQuiet.MaxLagMs < shardedHot.MaxLagMs,
            $"quiet {shardedQuiet.MaxLagMs}ms should be less than hot {shardedHot.MaxLagMs}ms");
        Assert.Contains(sharded.Shards, s => s.Processed > 0 && s.Id != shardedHot.Shard);
    }

    [Fact]
    public async Task Full_channel_records_backpressure()
    {
        var bus = new InMemoryEventBus();
        var counter = new SqueakCounter(
            bus,
            "bp",
            sequencerEnabled: false,
            shardCount: 1,
            handleDelay: TimeSpan.FromMilliseconds(15),
            shardCapacity: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = counter.RunAsync(cts.Token);
        var at = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 20; i++)
        {
            await bus.PublishAsync(
                SqueakedEnvelope.Create(new Squeaked("duck-1", i, at)),
                cts.Token);
        }

        await ConsumerWait.UntilCountAsync(counter, expected: 20, cts.Token);
        var snapshot = counter.ShardSnapshot;
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Shards[0].Backpressure > 0, "bounded channel should backpressure");

        cts.Cancel();
        await IgnoreCancel(run);
    }

    [Fact]
    public async Task Loud_duck_squeaks_far_more_than_the_others()
    {
        using var db = KernelDb.OpenInMemory();
        var state = new StateStore();
        var outbox = new OutboxStore();
        var publisher = new TransactionalPublisher(db, state, outbox);
        var simulator = new DuckSimulator(
            publisher,
            duckCount: 5,
            seed: 7,
            minDelayMs: 0,
            maxDelayMs: 0,
            loudDuckId: "duck-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await simulator.RunAsync(TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        db.Read(conn =>
        {
            foreach (var row in outbox.Unpublished(conn, 10_000))
            {
                var envelope = EnvelopeJson.Deserialize(row.PayloadJson);
                counts[envelope.PartitionKey] = counts.GetValueOrDefault(envelope.PartitionKey) + 1;
            }

            return 0;
        });

        Assert.True(simulator.PublishedCount >= 50, $"published {simulator.PublishedCount}");
        var loud = counts.GetValueOrDefault("duck-1");
        var quiet = counts.Where(kv => kv.Key != "duck-1").Select(kv => kv.Value).DefaultIfEmpty(0).Average();
        Assert.True(loud > quiet * 20, $"loud {loud} vs quiet avg {quiet}");
    }

    private static async Task<ShardMetricsSnapshot> RunBurstAsync(int shardCount)
    {
        var hot = PartitionShard.FirstKeyOnShard(0, 3);
        var quiet = PartitionShard.FirstKeyOnShard(1, 3);
        var bus = new InMemoryEventBus();
        var counter = new SqueakCounter(
            bus,
            "hot",
            sequencerEnabled: false,
            shardCount: shardCount,
            handleDelay: TimeSpan.FromMilliseconds(8),
            shardCapacity: 64);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = counter.RunAsync(cts.Token);
        var at = DateTimeOffset.UtcNow;
        const int hotCount = 30;
        for (var i = 1; i <= hotCount; i++)
        {
            await bus.PublishAsync(
                SqueakedEnvelope.Create(new Squeaked(hot, i, at)),
                cts.Token);
        }

        await bus.PublishAsync(
            SqueakedEnvelope.Create(new Squeaked(quiet, 1, at)),
            cts.Token);

        await ConsumerWait.UntilCountAsync(counter, expected: hotCount + 1, cts.Token);
        var snapshot = counter.ShardSnapshot;
        Assert.NotNull(snapshot);

        cts.Cancel();
        await IgnoreCancel(run);
        return snapshot;
    }

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
