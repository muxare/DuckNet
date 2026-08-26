using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Producer;

public sealed class DuckSimulator
{
    private readonly IEventBus _eventBus;
    private readonly int _duckCount;
    private readonly Random _random;
    private readonly Dictionary<string, long> _sequenceByDuck = new();

    public DuckSimulator(IEventBus eventBus, int duckCount, int? seed = null)
    {
        _eventBus = eventBus;
        _duckCount = duckCount;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var duckId = $"duck-{_random.Next(1, _duckCount + 1)}";
            var sequence = NextSequence(duckId);
            var squeaked = new Squeaked(duckId, sequence, DateTimeOffset.UtcNow);
            var envelope = SqueakedEnvelope.Create(squeaked);

            await _eventBus.PublishAsync(envelope, cancellationToken);
            PublishedCount++;
            await Task.Delay(_random.Next(10, 80), cancellationToken);
        }
    }

    private long NextSequence(string duckId)
    {
        if (!_sequenceByDuck.TryGetValue(duckId, out var sequence))
        {
            sequence = 0;
        }

        sequence++;
        _sequenceByDuck[duckId] = sequence;
        return sequence;
    }

    public long PublishedCount { get; private set; }

    public async Task PublishOneAsync(string duckId, CancellationToken cancellationToken = default)
    {
        var sequence = NextSequence(duckId);
        var squeaked = new Squeaked(duckId, sequence, DateTimeOffset.UtcNow);
        await _eventBus.PublishAsync(SqueakedEnvelope.Create(squeaked), cancellationToken);
        PublishedCount++;
    }
}
