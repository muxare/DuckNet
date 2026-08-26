using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Consumer;

public sealed class SqueakCounter
{
    private readonly IEventBus _eventBus;
    private readonly string _consumerGroup;
    private readonly int _logEvery;

    public SqueakCounter(IEventBus eventBus, string consumerGroup, int logEvery = 50)
    {
        _eventBus = eventBus;
        _consumerGroup = consumerGroup;
        _logEvery = logEvery;
    }

    public long TotalCount { get; private set; }

    public IReadOnlyDictionary<string, long> CountsByDuck => _countsByDuck;

    private readonly Dictionary<string, long> _countsByDuck = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _eventBus.SubscribeAsync(_consumerGroup, cancellationToken))
        {
            if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
            {
                continue;
            }

            var squeaked = SqueakedEnvelope.Parse(envelope);
            TotalCount++;
            _countsByDuck[squeaked.DuckId] = _countsByDuck.GetValueOrDefault(squeaked.DuckId) + 1;

            if (TotalCount % _logEvery == 0)
            {
                Console.WriteLine($"[SqueakCounter] processed={TotalCount}");
            }
        }
    }
}
