using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Producer;
using DuckNet.Kernel.Transport;

var seconds = ParseSeconds(args);
var duration = TimeSpan.FromSeconds(seconds);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"DuckNet Step 0 — running demo for {seconds}s (Ctrl+C to stop early)");

var eventBus = new InMemoryEventBus();
var counter = new SqueakCounter(eventBus, consumerGroup: "squeak-counter");
var simulator = new DuckSimulator(eventBus, duckCount: 5, seed: 42);

var consumerTask = counter.RunAsync(cts.Token);
var producerTask = simulator.RunAsync(duration, cts.Token);

await producerTask;

var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
while (counter.TotalCount < simulator.PublishedCount && DateTimeOffset.UtcNow < deadline)
{
    await Task.Delay(10);
}

cts.Cancel();
try
{
    await consumerTask;
}
catch (OperationCanceledException)
{
    // Step 0 consumer runs until cancelled — bus has no end-of-stream signal yet.
}

Console.WriteLine($"Total squeaks: {counter.TotalCount}");
foreach (var (duckId, count) in counter.CountsByDuck.OrderBy(x => x.Key))
{
    Console.WriteLine($"  {duckId}: {count}");
}

static int ParseSeconds(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds))
        {
            return seconds;
        }

        if (args[i].StartsWith("--seconds=", StringComparison.Ordinal)
            && int.TryParse(args[i]["--seconds=".Length..], out seconds))
        {
            return seconds;
        }
    }

    return 30;
}
