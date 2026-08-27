namespace DuckNet.Kernel.Producer;

public sealed class DuckSimulator
{
    private readonly TransactionalPublisher _publisher;
    private readonly int _duckCount;
    private readonly Random _random;

    public DuckSimulator(TransactionalPublisher publisher, int duckCount, int? seed = null)
    {
        _publisher = publisher;
        _duckCount = duckCount;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var duckId = $"duck-{_random.Next(1, _duckCount + 1)}";
            await _publisher.PublishSqueakAsync(duckId, cancellationToken);
            PublishedCount++;
            await Task.Delay(_random.Next(10, 80), cancellationToken);
        }
    }

    public long PublishedCount { get; private set; }

    public async Task PublishOneAsync(string duckId, CancellationToken cancellationToken = default)
    {
        await _publisher.PublishSqueakAsync(duckId, cancellationToken);
        PublishedCount++;
    }
}
