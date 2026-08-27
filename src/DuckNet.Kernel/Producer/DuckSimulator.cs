namespace DuckNet.Kernel.Producer;

public sealed class DuckSimulator
{
    private readonly TransactionalPublisher _publisher;
    private readonly int _duckCount;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random;

    public DuckSimulator(
        TransactionalPublisher publisher,
        int duckCount,
        int? seed = null,
        int minDelayMs = 10,
        int maxDelayMs = 80)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duckCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minDelayMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelayMs, minDelayMs);

        _publisher = publisher;
        _duckCount = duckCount;
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = duration == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var duckId = $"duck-{_random.Next(1, _duckCount + 1)}";
            await _publisher.PublishSqueakAsync(duckId, cancellationToken);
            PublishedCount++;
            await Task.Delay(_random.Next(_minDelayMs, _maxDelayMs + 1), cancellationToken);
        }
    }

    public long PublishedCount { get; private set; }

    public async Task PublishOneAsync(string duckId, CancellationToken cancellationToken = default)
    {
        await _publisher.PublishSqueakAsync(duckId, cancellationToken);
        PublishedCount++;
    }
}
