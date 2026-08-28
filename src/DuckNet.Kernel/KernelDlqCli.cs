using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel;

public static class KernelDlqCli
{
    public const string ConsumerGroup = "squeak-counter";

    public static int List(string databasePath, TextWriter output)
    {
        using var db = KernelDb.Open(databasePath);
        var store = new DeadLetterStore();
        var rows = db.Read(conn => store.List(conn, ConsumerGroup));
        output.WriteLine($"DLQ rows: {rows.Count} (db={databasePath})");
        foreach (var row in rows)
        {
            output.WriteLine(
                $"  id={row.Id} eventId={row.EventId} attempts={row.Attempts} failedAt={row.FailedAt:O}");
            output.WriteLine($"    error: {row.Error}");
            output.WriteLine($"    payload: {row.PayloadJson}");
        }

        return 0;
    }

    public static int Replay(string databasePath, long id, bool fix, TextWriter output)
    {
        using var db = KernelDb.Open(databasePath);
        var counter = CreateCounter(db, output);
        if (!counter.TryReplay(id, fix))
        {
            output.WriteLine($"Replay failed for DLQ id={id} (missing or still poison). Use --fix to rewrite payload.");
            return 1;
        }

        output.WriteLine($"Replayed DLQ id={id} fix={fix} counted={counter.TotalCount}");
        return 0;
    }

    public static int Skip(string databasePath, long id, TextWriter output)
    {
        using var db = KernelDb.Open(databasePath);
        var counter = CreateCounter(db, output);
        if (!counter.TrySkip(id))
        {
            output.WriteLine($"No DLQ row id={id}");
            return 1;
        }

        output.WriteLine($"Skipped DLQ id={id}");
        return 0;
    }

    private static SqueakCounter CreateCounter(KernelDb db, TextWriter output)
    {
        var inbox = new Inbox(ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, ConsumerGroup);
        var counts = new SqueakCountStore();
        var restored = db.Read(conn => counts.Load(conn, ConsumerGroup));
        var checkpoint = new ConsumerCheckpoint(db, inbox, offsets, counts);
        return new SqueakCounter(
            new InMemoryEventBus(),
            ConsumerGroup,
            inbox,
            logEvery: int.MaxValue,
            output: output,
            sequencerEnabled: false,
            checkpoint: checkpoint,
            restoredCounts: restored,
            retry: new RetryPipeline(maxAttempts: 1, baseDelay: TimeSpan.Zero),
            deadLetters: new DeadLetterStore(),
            db: db);
    }
}
