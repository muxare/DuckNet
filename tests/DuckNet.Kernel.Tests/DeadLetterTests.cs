using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Tests;

public class DeadLetterTests
{
    [Fact]
    public void Store_insert_and_delete_round_trip()
    {
        using var db = KernelDb.OpenInMemory();
        var store = new DeadLetterStore();
        var envelope = PoisonEvents.MalformedSqueaked();

        var id = db.Write((conn, tx) =>
            store.Insert(conn, tx, "g", envelope, "JsonException: bad", 5));
        Assert.Equal(1, db.Read(conn => store.Count(conn, "g")));

        Assert.True(db.Write((conn, tx) => store.Delete(conn, tx, id)));
        Assert.Equal(0, db.Read(conn => store.Count(conn, "g")));
    }

    [Fact]
    public async Task Poison_on_a_partition_does_not_block_later_seq()
    {
        using var db = KernelDb.OpenInMemory();
        var bus = new InMemoryEventBus();
        const string group = "test";
        var inbox = new Inbox(group, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var counts = new SqueakCountStore();
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var dlq = new DeadLetterStore();
        var log = new StringWriter();
        var counter = new SqueakCounter(
            bus,
            group,
            inbox,
            logEvery: int.MaxValue,
            output: log,
            checkpoint: checkpoint,
            retry: new RetryPipeline(maxAttempts: 3, baseDelay: TimeSpan.Zero),
            deadLetters: dlq,
            db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        var at = DateTimeOffset.UtcNow;
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-1", 1, at)), cts.Token);
        await bus.PublishAsync(PoisonEvents.MalformedSqueaked("duck-1", 2), cts.Token);
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-1", 3, at)), cts.Token);

        await ConsumerWait.UntilCountAsync(counter, expected: 2, cts.Token);
        await ConsumerWait.UntilDeadLettersAsync(counter, expected: 1, cts.Token);

        Assert.Equal(2, counter.TotalCount);
        Assert.Equal(1, counter.DeadLetteredCount);
        Assert.Equal(2, counter.CountsByDuck["duck-1"]);

        var rows = db.Read(conn => dlq.List(conn, group));
        Assert.Single(rows);
        Assert.Contains("JsonException", rows[0].Error, StringComparison.Ordinal);
        Assert.Contains(PoisonEvents.PayloadJson, rows[0].PayloadJson, StringComparison.Ordinal);
        Assert.Equal(3, rows[0].Attempts);
        Assert.Contains("Dead-letter", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_after_fix_applies_the_skipped_squeak()
    {
        using var db = KernelDb.OpenInMemory();
        var bus = new InMemoryEventBus();
        const string group = "test";
        var inbox = new Inbox(group, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var counts = new SqueakCountStore();
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var dlq = new DeadLetterStore();
        var counter = new SqueakCounter(
            bus,
            group,
            inbox,
            logEvery: int.MaxValue,
            checkpoint: checkpoint,
            retry: new RetryPipeline(maxAttempts: 2, baseDelay: TimeSpan.Zero),
            deadLetters: dlq,
            db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        var at = DateTimeOffset.UtcNow;
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-1", 1, at)), cts.Token);
        await bus.PublishAsync(PoisonEvents.MalformedSqueaked("duck-1", 2), cts.Token);
        await bus.PublishAsync(SqueakedEnvelope.Create(new Squeaked("duck-1", 3, at)), cts.Token);

        await ConsumerWait.UntilCountAsync(counter, expected: 2, cts.Token);
        await ConsumerWait.UntilDeadLettersAsync(counter, expected: 1, cts.Token);

        cts.Cancel();
        await Task.Delay(20);

        var id = db.Read(conn => dlq.List(conn, group).Single().Id);
        Assert.True(counter.TryReplay(id, fix: true));

        Assert.Equal(3, counter.TotalCount);
        Assert.Equal(3, counter.CountsByDuck["duck-1"]);
        Assert.Equal(0, db.Read(conn => dlq.Count(conn, group)));
    }

    [Fact]
    public async Task Replay_without_fix_leaves_the_row()
    {
        using var db = KernelDb.OpenInMemory();
        var bus = new InMemoryEventBus();
        const string group = "test";
        var inbox = new Inbox(group, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var counts = new SqueakCountStore();
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var dlq = new DeadLetterStore();
        var counter = new SqueakCounter(
            bus,
            group,
            inbox,
            checkpoint: checkpoint,
            retry: new RetryPipeline(maxAttempts: 2, baseDelay: TimeSpan.Zero),
            deadLetters: dlq,
            db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await bus.PublishAsync(PoisonEvents.MalformedSqueaked(), cts.Token);
        await ConsumerWait.UntilDeadLettersAsync(counter, expected: 1, cts.Token);

        var id = db.Read(conn => dlq.List(conn, group).Single().Id);
        Assert.False(counter.TryReplay(id, fix: false));
        Assert.Equal(1, db.Read(conn => dlq.Count(conn, group)));
        Assert.Equal(0, counter.TotalCount);
    }

    [Fact]
    public async Task Skip_removes_the_row_without_counting()
    {
        using var db = KernelDb.OpenInMemory();
        var bus = new InMemoryEventBus();
        const string group = "test";
        var inbox = new Inbox(group, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var counts = new SqueakCountStore();
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var dlq = new DeadLetterStore();
        var counter = new SqueakCounter(
            bus,
            group,
            inbox,
            checkpoint: checkpoint,
            retry: new RetryPipeline(maxAttempts: 2, baseDelay: TimeSpan.Zero),
            deadLetters: dlq,
            db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        await bus.PublishAsync(PoisonEvents.MalformedSqueaked(), cts.Token);
        await ConsumerWait.UntilDeadLettersAsync(counter, expected: 1, cts.Token);

        var id = db.Read(conn => dlq.List(conn, group).Single().Id);
        Assert.True(counter.TrySkip(id));
        Assert.Equal(0, db.Read(conn => dlq.Count(conn, group)));
        Assert.Equal(0, counter.TotalCount);
    }

    [Fact]
    public async Task Kernel_runner_poison_lands_in_dlq_and_counts_still_match()
    {
        var result = await KernelRunner.RunDemoAsync(
            TimeSpan.FromMilliseconds(400),
            duckCount: 3,
            seed: 42,
            duplicateRate: 0,
            shuffleEnabled: false,
            injectPoison: true);

        Assert.True(result.PublishedCount > 0);
        Assert.Equal(result.PublishedCount, result.TotalCount);
        Assert.Equal(result.PublishedCount + 1, result.LogCount);
        Assert.Equal(1, result.DeadLetteredCount);
        Assert.Equal(0, result.OutOfOrderCount);
    }

    [Fact]
    public async Task Kernel_cli_replay_fix_then_skip_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-dlq-cli-{Guid.NewGuid():N}.db");
        try
        {
            var result = await KernelRunner.RunDemoAsync(
                TimeSpan.FromMilliseconds(200),
                duckCount: 2,
                seed: 1,
                duplicateRate: 0,
                shuffleEnabled: false,
                databasePath: path,
                injectPoison: true);
            Assert.Equal(1, result.DeadLetteredCount);

            var log = new StringWriter();
            Assert.Equal(0, KernelDlqCli.List(path, log));
            Assert.Contains("DLQ rows: 1", log.ToString(), StringComparison.Ordinal);

            var id = ReadFirstDlqId(path);
            Assert.Equal(0, KernelDlqCli.Replay(path, id, fix: true, log));
            Assert.Equal(0, ReadDlqCount(path));
        }
        finally
        {
            KernelRunner.DeleteSqliteFiles(path);
        }
    }

    private static long ReadFirstDlqId(string path)
    {
        using var db = KernelDb.Open(path);
        var store = new DeadLetterStore();
        return db.Read(conn => store.List(conn, KernelDlqCli.ConsumerGroup).Single().Id);
    }

    private static long ReadDlqCount(string path)
    {
        using var db = KernelDb.Open(path);
        var store = new DeadLetterStore();
        return db.Read(conn => store.Count(conn, KernelDlqCli.ConsumerGroup));
    }
}
