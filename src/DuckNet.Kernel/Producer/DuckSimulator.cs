using System.Diagnostics;
using DuckNet.EventBus;

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
    private readonly string[] _otherDuckIds;
    private readonly ActivitySource _activitySource;

    public DuckSimulator(
        TransactionalPublisher publisher,
        int duckCount,
        int? seed = null,
        int minDelayMs = 10,
        int maxDelayMs = 80,
        string? loudDuckId = null,
        int loudWeight = DefaultLoudWeight,
        ActivitySource? activitySource = null)
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
        _activitySource = activitySource ?? DuckNetTracing.Kernel;
        _otherDuckIds = _loudDuckId is null
            ? []
            : Enumerable.Range(1, duckCount)
                .Select(i => $"duck-{i}")
                .Where(id => !string.Equals(id, _loudDuckId, StringComparison.Ordinal))
                .ToArray();
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = duration == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var duckId = NextDuckId();
            await EmitAsync(duckId, cancellationToken);
            await Task.Delay(_random.Next(_minDelayMs, _maxDelayMs + 1), cancellationToken);
        }
    }

    public long PublishedCount { get; private set; }

    public async Task PublishOneAsync(string duckId, CancellationToken cancellationToken = default)
    {
        await EmitAsync(duckId, cancellationToken);
    }

    public string? LoudDuckId => _loudDuckId;

    private async Task EmitAsync(string duckId, CancellationToken cancellationToken)
    {
        using var activity = DuckNetTracing.StartProducer(_activitySource, "simulate.squeak", duckId);
        await _publisher.PublishSqueakAsync(duckId, NextVolumeDb(), cancellationToken);
        PublishedCount++;
    }

    private string NextDuckId()
    {
        if (_loudDuckId is null)
        {
            return $"duck-{_random.Next(1, _duckCount + 1)}";
        }

        var roll = _random.Next(_loudWeight + _otherDuckIds.Length);
        return roll < _loudWeight ? _loudDuckId : _otherDuckIds[roll - _loudWeight];
    }

    private double NextVolumeDb() => 50 + (_random.NextDouble() * 40);
}
