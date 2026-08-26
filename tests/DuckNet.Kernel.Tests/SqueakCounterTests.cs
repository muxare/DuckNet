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

        await WaitForCountAsync(counter, expected: 3, cts.Token);

        Assert.Equal(3, counter.TotalCount);
        Assert.Equal(2, counter.CountsByDuck["duck-1"]);
        Assert.Equal(1, counter.CountsByDuck["duck-2"]);
    }

    [Fact]
    public async Task Demo_runner_counts_match_published()
    {
        var result = await KernelRunner.RunDemoAsync(
            TimeSpan.FromMilliseconds(500),
            duckCount: 3,
            seed: 42);

        Assert.True(result.TotalCount > 0);
        Assert.Equal(result.TotalCount, result.CountsByDuck.Values.Sum());
    }

    [Fact]
    public void Producer_does_not_depend_on_consumer_types()
    {
        var ctor = typeof(DuckSimulator).GetConstructors().Single();
        Assert.All(ctor.GetParameters(), p =>
            Assert.DoesNotContain("Consumer", p.ParameterType.FullName ?? string.Empty));
    }

    private static async Task WaitForCountAsync(
        SqueakCounter counter,
        long expected,
        CancellationToken cancellationToken)
    {
        while (counter.TotalCount < expected)
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
