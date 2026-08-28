namespace DuckNet.Kernel.Producer;

public sealed class DuckSimulator
{
    public const int DefaultLoudWeight = 100;

    private readonly TransactionalPublisher _publisher;
    private readonly int _duckCount;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random;
    private readonly string? _loudDuckId;
    private readonly int _loudWeight;

    public DuckSimulator(
        TransactionalPublisher publisher,
        int duckCount,
        int? seed = null,
        int minDelayMs = 10,
        int maxDelayMs = 80,
        string? loudDuckId = null,
        int loudWeight = DefaultLoudWeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duckCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minDelayMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelayMs, minDelayMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(loudWeight, 1);

        _publisher = publisher;
        _duckCount = duckCount;
        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        _loudDuckId = string.IsNullOrWhiteSpace(loudDuckId) ? null : loudDuckId;
        _loudWeight = loudWeight;
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = duration == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var duckId = NextDuckId();
            await _publisher.PublishSqueakAsync(duckId, NextVolumeDb(), cancellationToken);
            PublishedCount++;
            await Task.Delay(_random.Next(_minDelayMs, _maxDelayMs + 1), cancellationToken);
        }
    }

    public long PublishedCount { get; private set; }

    public async Task PublishOneAsync(string duckId, CancellationToken cancellationToken = default)
    {
        await _publisher.PublishSqueakAsync(duckId, NextVolumeDb(), cancellationToken);
        PublishedCount++;
    }

    public string? LoudDuckId => _loudDuckId;

    private string NextDuckId()
    {
        if (_loudDuckId is null)
        {
            return $"duck-{_random.Next(1, _duckCount + 1)}";
        }

        var otherCount = 0;
        for (var i = 1; i <= _duckCount; i++)
        {
            if (!string.Equals($"duck-{i}", _loudDuckId, StringComparison.Ordinal))
            {
                otherCount++;
            }
        }

        var roll = _random.Next(_loudWeight + otherCount);
        if (roll < _loudWeight)
        {
            return _loudDuckId;
        }

        var skip = roll - _loudWeight;
        for (var i = 1; i <= _duckCount; i++)
        {
            var id = $"duck-{i}";
            if (string.Equals(id, _loudDuckId, StringComparison.Ordinal))
            {
                continue;
            }

            if (skip == 0)
            {
                return id;
            }

            skip--;
        }

        return _loudDuckId;
    }

    private double NextVolumeDb() => 50 + (_random.NextDouble() * 40);
}
