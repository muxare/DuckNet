using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Hostile transport: unordered across keys. Windowed shuffle — never a global order guarantee.
/// Remainder is released by <see cref="FlushAsync"/> so short demos still shuffle.
/// </summary>
public sealed class ShufflerMiddleware : IEventBus
{
    private readonly IEventBus _inner;
    private readonly int _windowSize;
    private readonly Random _random;
    private readonly bool _enabled;
    private readonly List<EventEnvelope> _buffer = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _flushCount;

    public ShufflerMiddleware(
        IEventBus inner,
        int windowSize = 50,
        int? seed = null,
        bool enabled = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);

        _inner = inner;
        _windowSize = windowSize;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        _enabled = enabled;
    }

    public long FlushCount => Interlocked.Read(ref _flushCount);

    public async ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_enabled || _windowSize <= 1)
        {
            await _inner.PublishAsync(envelope, cancellationToken);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _buffer.Add(envelope);
            if (_buffer.Count >= _windowSize)
            {
                await FlushBufferUnlocked(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushBufferUnlocked(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default) =>
        _inner.SubscribeAsync(consumerGroup, cancellationToken);

    private async Task FlushBufferUnlocked(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        Shuffle(_buffer);
        foreach (var envelope in _buffer)
        {
            await _inner.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        _buffer.Clear();
        Interlocked.Increment(ref _flushCount);
    }

    private void Shuffle(List<EventEnvelope> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
