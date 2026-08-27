using System.Collections.Concurrent;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Hostile transport: at-least-once redelivery. Clones keep the same <c>EventId</c>
/// so the consumer inbox, not the bus, is responsible for idempotency.
/// </summary>
public sealed class DuplicatorMiddleware : IEventBus
{
    private readonly IEventBus _inner;
    private readonly double _duplicateRate;
    private readonly Random _random;
    private readonly TimeSpan _maxDelay;
    private readonly ConcurrentBag<Task> _pending = [];
    private long _duplicateCount;

    public DuplicatorMiddleware(
        IEventBus inner,
        double duplicateRate = 0.15,
        int? seed = null,
        TimeSpan? maxDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duplicateRate, 1.0);

        _inner = inner;
        _duplicateRate = duplicateRate;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        _maxDelay = maxDelay ?? TimeSpan.Zero;
    }

    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    public async ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await _inner.PublishAsync(envelope, cancellationToken);

        if (_duplicateRate <= 0 || _random.NextDouble() >= _duplicateRate)
        {
            return;
        }

        Interlocked.Increment(ref _duplicateCount);

        if (_maxDelay <= TimeSpan.Zero)
        {
            await _inner.PublishAsync(envelope, cancellationToken);
            return;
        }

        var delayMs = _random.Next(1, Math.Max(2, (int)_maxDelay.TotalMilliseconds + 1));
        _pending.Add(ReenqueueAfterDelayAsync(envelope, TimeSpan.FromMilliseconds(delayMs)));
    }

    public Task FlushAsync() => Task.WhenAll(_pending);

    public IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default) =>
        _inner.SubscribeAsync(consumerGroup, cancellationToken);

    private async Task ReenqueueAfterDelayAsync(EventEnvelope envelope, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        await _inner.PublishAsync(envelope).ConfigureAwait(false);
    }
}
