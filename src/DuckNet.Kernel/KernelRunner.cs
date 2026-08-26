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
        CancellationToken cancellationToken = default)
    {
        var eventBus = new InMemoryEventBus();
        var counter = new SqueakCounter(eventBus, consumerGroup: "squeak-counter", logEvery: int.MaxValue);
        var simulator = new DuckSimulator(eventBus, duckCount, seed);

        var consumerTask = counter.RunAsync(cancellationToken);
        await simulator.RunAsync(duration, cancellationToken);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (counter.TotalCount < simulator.PublishedCount && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        return new RunResult(counter.TotalCount, new Dictionary<string, long>(counter.CountsByDuck));
    }
}

public sealed record RunResult(long TotalCount, IReadOnlyDictionary<string, long> CountsByDuck);
