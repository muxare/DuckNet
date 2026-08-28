using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel;

public static class KernelRunner
{
    public static async Task<RunResult> RunDemoAsync(
        TimeSpan duration,
        int duckCount = 5,
        int? seed = 42,
        double duplicateRate = 0.15,
        bool inboxEnabled = true,
        int logEvery = int.MaxValue,
        bool logDuplicates = false,
        TextWriter? output = null,
        TimeSpan? duplicateMaxDelay = null,
        bool shuffleEnabled = true,
        int shuffleWindow = 50,
        bool sequencerEnabled = true,
        string? databasePath = null,
        bool resetDatabase = false,
        bool injectPoison = false,
        TimeSpan? retryBaseDelay = null,
        int shardCount = PartitionShard.DefaultCount,
        TimeSpan? handleDelay = null,
        string? loudDuckId = null,
        int minDelayMs = 10,
        int maxDelayMs = 80,
        CancellationToken cancellationToken = default)
    {
        var path = databasePath ?? Path.Combine(Path.GetTempPath(), $"ducknet-{Guid.NewGuid():N}.db");
        if (resetDatabase)
        {
            DeleteSqliteFiles(path);
        }

        using var db = KernelDb.Open(path);
        var state = new StateStore();
        var outbox = new OutboxStore();
        var log = new EventLogStore();
        var counts = new SqueakCountStore();
        var publisher = new TransactionalPublisher(db, state, outbox);
        var simulator = new DuckSimulator(
            publisher,
            duckCount,
            seed,
            minDelayMs,
            maxDelayMs,
            loudDuckId);

        var inner = new InMemoryEventBus();
        var shuffler = new ShufflerMiddleware(inner, shuffleWindow, seed, shuffleEnabled);
        var eventBus = new DuplicatorMiddleware(
            shuffler,
            duplicateRate,
            seed,
            duplicateMaxDelay ?? TimeSpan.Zero);

        const string group = "squeak-counter";
        var inbox = new Inbox(group, inboxEnabled, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var restored = db.Read(conn => counts.Load(conn, group));
        var lastSeq = restored.ToDictionary(x => x.Key, x => x.Value.LastSeq);
        var sequencer = sequencerEnabled ? new PerKeySequencer(lastSeq) : null;
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var deadLetters = new DeadLetterStore();
        var retry = new RetryPipeline(
            baseDelay: retryBaseDelay ?? TimeSpan.Zero,
            sleep: _ => { });
        var counter = new SqueakCounter(
            eventBus,
            consumerGroup: group,
            inbox,
            logEvery,
            logDuplicates,
            output,
            sequencer,
            sequencerEnabled,
            checkpoint: checkpoint,
            restoredCounts: restored,
            retry: retry,
            deadLetters: deadLetters,
            db: db,
            shardCount: shardCount,
            handleDelay: handleDelay);

        var dispatcher = new OutboxDispatcher(db, outbox, log);
        var feeder = new LogTailFeeder(db, log, eventBus, startOffset: offsets.LastOffset);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dispatcherTask = dispatcher.RunAsync(linked.Token);
        var feederTask = feeder.RunAsync(linked.Token);
        var consumerTask = counter.RunAsync(linked.Token);

        await simulator.RunAsync(duration, cancellationToken);
        await dispatcher.DrainAsync(cancellationToken);
        if (injectPoison)
        {
            db.Write((conn, tx) => log.Append(conn, tx, PoisonEvents.MalformedSqueaked()));
        }

        await feeder.CatchUpAsync(cancellationToken);
        await eventBus.FlushAsync();
        await shuffler.FlushAsync();

        var expectedAttempts = simulator.PublishedCount + eventBus.DuplicateCount + (injectPoison ? 1 : 0);
        var drainBudget = TimeSpan.FromSeconds(5);
        if (handleDelay is { } delay && delay > TimeSpan.Zero)
        {
            drainBudget += TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Max(expectedAttempts, 1));
        }

        var deadline = DateTimeOffset.UtcNow + drainBudget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await counter.DrainAsync(cancellationToken);
            if (counter.AttemptCount >= expectedAttempts
                && (!injectPoison || counter.DeadLetteredCount >= 1))
            {
                break;
            }

            await Task.Delay(10, cancellationToken);
        }

        var shards = counter.ShardSnapshot;
        linked.Cancel();
        await IgnoreCancel(dispatcherTask);
        await IgnoreCancel(feederTask);
        await IgnoreCancel(consumerTask);

        var logCount = db.Read(conn => log.Count(conn));
        var dlqCount = db.Read(conn => deadLetters.Count(conn, group));
        return new RunResult(
            counter.TotalCount,
            simulator.PublishedCount,
            eventBus.DuplicateCount,
            inbox.DuplicateSkipCount,
            sequencer?.LateDropCount ?? 0,
            counter.OutOfOrderCount,
            new Dictionary<string, long>(counter.CountsByDuck),
            logCount,
            offsets.LastOffset,
            path,
            dlqCount,
            shards);
    }

    public static void DeleteSqliteFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Background loops run until cancelled.
        }
    }
}

public sealed record RunResult(
    long TotalCount,
    long PublishedCount,
    long DuplicateDeliveries,
    long DuplicateSkips,
    long SequencerLateDrops,
    long OutOfOrderCount,
    IReadOnlyDictionary<string, long> CountsByDuck,
    long LogCount,
    long LastOffset,
    string DatabasePath,
    long DeadLetteredCount = 0,
    ShardMetricsSnapshot? Shards = null);
