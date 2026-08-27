using DuckNet.Kernel.Consumer;
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
        CancellationToken cancellationToken = default)
    {
        var inner = new InMemoryEventBus();
        var shuffler = new ShufflerMiddleware(inner, shuffleWindow, seed, shuffleEnabled);
        var eventBus = new DuplicatorMiddleware(
            shuffler,
            duplicateRate,
            seed,
            duplicateMaxDelay ?? TimeSpan.Zero);
        var inbox = new Inbox("squeak-counter", inboxEnabled);
        var sequencer = sequencerEnabled ? new PerKeySequencer() : null;
        var counter = new SqueakCounter(
            eventBus,
            consumerGroup: "squeak-counter",
            inbox,
            logEvery,
            logDuplicates,
            output,
            sequencer,
            sequencerEnabled);
        var simulator = new DuckSimulator(eventBus, duckCount, seed);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consumerTask = counter.RunAsync(linked.Token);
        await simulator.RunAsync(duration, cancellationToken);
        await eventBus.FlushAsync();
        await shuffler.FlushAsync();

        var expectedAttempts = simulator.PublishedCount + eventBus.DuplicateCount;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (counter.AttemptCount < expectedAttempts && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        linked.Cancel();
        try
        {
            await consumerTask;
        }
        catch (OperationCanceledException)
        {
            // Consumer runs until cancelled — bus has no end-of-stream signal yet.
        }

        return new RunResult(
            counter.TotalCount,
            simulator.PublishedCount,
            eventBus.DuplicateCount,
            inbox.DuplicateSkipCount,
            sequencer?.LateDropCount ?? 0,
            counter.OutOfOrderCount,
            new Dictionary<string, long>(counter.CountsByDuck));
    }
}

public sealed record RunResult(
    long TotalCount,
    long PublishedCount,
    long DuplicateDeliveries,
    long DuplicateSkips,
    long SequencerLateDrops,
    long OutOfOrderCount,
    IReadOnlyDictionary<string, long> CountsByDuck);
