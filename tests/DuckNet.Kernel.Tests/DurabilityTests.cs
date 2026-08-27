using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class DurabilityTests
{
    [Fact]
    public void Uncommitted_transaction_writes_neither_state_nor_outbox()
    {
        using var db = KernelDb.OpenInMemory();
        var state = new StateStore();
        var outbox = new OutboxStore();
        var envelope = SqueakedEnvelope.Create(new Squeaked("duck-1", 1, DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(() => db.Write((conn, tx) =>
        {
            state.NextSequence(conn, tx, "duck-1");
            outbox.Insert(conn, tx, envelope);
            throw new InvalidOperationException("crash");
        }));

        db.Read(conn =>
        {
            Assert.Equal(0, state.Get(conn, "duck-1"));
            Assert.Empty(outbox.Unpublished(conn, 10));
            return 0;
        });
    }

    [Fact]
    public async Task Dispatcher_appends_log_and_marks_outbox_published()
    {
        using var db = KernelDb.OpenInMemory();
        var state = new StateStore();
        var outbox = new OutboxStore();
        var log = new EventLogStore();
        var publisher = new TransactionalPublisher(db, state, outbox);
        var dispatcher = new OutboxDispatcher(db, outbox, log);

        await publisher.PublishSqueakAsync("duck-1");
        await publisher.PublishSqueakAsync("duck-1");
        await dispatcher.DrainAsync();

        db.Read(conn =>
        {
            Assert.Equal(2, log.Count(conn));
            Assert.Equal(0, outbox.UnpublishedCount(conn));
            Assert.Equal(2, state.Get(conn, "duck-1"));
            var rows = log.ReadAfter(conn, 0, 10);
            Assert.Equal(new[] { 1L, 2L }, rows.Select(e => e.SequenceNumber));
            Assert.Equal(new[] { 1L, 2L }, rows.Select(e => e.LogOffset));
            return 0;
        });
    }

    [Fact]
    public async Task Consumer_restart_resumes_from_offset_without_double_count()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-restart-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = KernelDb.Open(path))
            {
                await ProduceAsync(db, count: 10);
                var (counter, offsets) = await RunConsumerAsync(db, feedLimit: 4, expectedTotal: 4);
                Assert.Equal(4, counter.TotalCount);
                Assert.Equal(4, offsets.LastOffset);
            }

            using (var db = KernelDb.Open(path))
            {
                var offsets = new ConsumerOffsetStore(db, "squeak-counter");
                Assert.Equal(4, offsets.LastOffset);

                var (counter, _) = await RunConsumerAsync(db, feedLimit: 50, expectedTotal: 10);
                Assert.Equal(10, counter.TotalCount);
                Assert.Equal(0, counter.OutOfOrderCount);
            }
        }
        finally
        {
            KernelRunner.DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task Full_log_replay_from_offset_zero_reproduces_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-replay-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = KernelDb.Open(path))
            {
                await ProduceAsync(db, count: 8);
                await RunConsumerAsync(db, feedLimit: 50, expectedTotal: 8);
            }

            using (var db = KernelDb.Open(path))
            {
                db.Write((conn, tx) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM inbox; DELETE FROM consumer_offsets; DELETE FROM squeak_counts;";
                    cmd.ExecuteNonQuery();
                });

                var (counter, _) = await RunConsumerAsync(db, feedLimit: 50, expectedTotal: 8);
                Assert.Equal(8, counter.TotalCount);
                Assert.Equal(0, counter.OutOfOrderCount);
            }
        }
        finally
        {
            KernelRunner.DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task Replay_with_populated_inbox_does_not_change_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-replay-inbox-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = KernelDb.Open(path))
            {
                await ProduceAsync(db, count: 6);
                await RunConsumerAsync(db, feedLimit: 50, expectedTotal: 6);
            }

            using (var db = KernelDb.Open(path))
            {
                db.Write((conn, tx) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM consumer_offsets";
                    cmd.ExecuteNonQuery();
                });

                var (counter, _) = await RunConsumerAsync(db, feedLimit: 50, expectedTotal: 6);
                Assert.Equal(6, counter.TotalCount);
            }
        }
        finally
        {
            KernelRunner.DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task Kernel_runner_second_session_on_same_db_continues_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-session-{Guid.NewGuid():N}.db");
        try
        {
            var first = await KernelRunner.RunDemoAsync(
                TimeSpan.FromMilliseconds(400),
                duckCount: 3,
                seed: 7,
                duplicateRate: 0.2,
                shuffleEnabled: true,
                shuffleWindow: 8,
                databasePath: path);

            var second = await KernelRunner.RunDemoAsync(
                TimeSpan.FromMilliseconds(400),
                duckCount: 3,
                seed: 8,
                duplicateRate: 0.2,
                shuffleEnabled: true,
                shuffleWindow: 8,
                databasePath: path);

            Assert.True(first.PublishedCount > 0);
            Assert.True(second.PublishedCount > 0);
            Assert.Equal(first.PublishedCount, first.TotalCount);
            Assert.Equal(first.PublishedCount + second.PublishedCount, second.TotalCount);
            Assert.Equal(second.LogCount, second.TotalCount);
            Assert.Equal(0, second.OutOfOrderCount);
        }
        finally
        {
            KernelRunner.DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public void Offset_advances_contiguous_prefix_when_shuffled()
    {
        using var db = KernelDb.OpenInMemory();
        var offsets = new ConsumerOffsetStore(db, "g");

        db.Write((conn, tx) =>
        {
            offsets.MarkProcessed(conn, tx, 3);
            Assert.Equal(0, offsets.LastOffset);
            offsets.MarkProcessed(conn, tx, 1);
            Assert.Equal(1, offsets.LastOffset);
            offsets.MarkProcessed(conn, tx, 2);
            Assert.Equal(3, offsets.LastOffset);
        });

        db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_offset FROM consumer_offsets WHERE consumer_group = 'g'";
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
            return 0;
        });
    }

    private static async Task ProduceAsync(KernelDb db, int count)
    {
        var publisher = new TransactionalPublisher(db, new StateStore(), new OutboxStore());
        for (var i = 0; i < count; i++)
        {
            await publisher.PublishSqueakAsync($"duck-{(i % 3) + 1}");
        }

        await new OutboxDispatcher(db, new OutboxStore(), new EventLogStore()).DrainAsync();
    }

    private static async Task<(SqueakCounter Counter, ConsumerOffsetStore Offsets)> RunConsumerAsync(
        KernelDb db,
        int feedLimit,
        long expectedTotal)
    {
        var inner = new InMemoryEventBus();
        const string group = "squeak-counter";
        var inbox = new Inbox(group, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, group);
        var counts = new SqueakCountStore();
        var restored = db.Read(conn => counts.Load(conn, group));
        var lastSeq = restored.ToDictionary(x => x.Key, x => x.Value.LastSeq);
        var sequencer = new PerKeySequencer(lastSeq);
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        var counter = new SqueakCounter(
            inner,
            group,
            inbox,
            sequencer: sequencer,
            checkpoint: checkpoint,
            restoredCounts: restored);
        var feeder = new LogTailFeeder(db, new EventLogStore(), inner, startOffset: offsets.LastOffset);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var consumerTask = counter.RunAsync(cts.Token);

        if (counter.TotalCount < expectedTotal)
        {
            var fed = 0;
            while (counter.TotalCount < expectedTotal)
            {
                var remaining = feedLimit - fed;
                if (remaining <= 0)
                {
                    await Task.Delay(10, cts.Token);
                    continue;
                }

                var n = await feeder.FeedBatchAsync(cts.Token, Math.Min(remaining, 50));
                fed += n;
                if (n == 0)
                {
                    await Task.Delay(10, cts.Token);
                }
            }
        }
        else
        {
            await feeder.CatchUpAsync(cts.Token);
            await ConsumerWait.UntilAttemptsAsync(counter, expectedTotal, cts.Token);
        }

        cts.Cancel();
        await IgnoreCancel(consumerTask);
        return (counter, offsets);
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
