using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Consumer;

public sealed class SqueakCounter
{
    private readonly IEventBus _eventBus;
    private readonly string _consumerGroup;
    private readonly Inbox _inbox;
    private readonly int _logEvery;
    private readonly bool _logDuplicates;
    private readonly TextWriter _output;
    private readonly Dictionary<string, long> _countsByDuck = new();

    public SqueakCounter(
        IEventBus eventBus,
        string consumerGroup,
        Inbox? inbox = null,
        int logEvery = 50,
        bool logDuplicates = false,
        TextWriter? output = null)
    {
        _eventBus = eventBus;
        _consumerGroup = consumerGroup;
        _inbox = inbox ?? new Inbox(consumerGroup);
        _logEvery = logEvery;
        _logDuplicates = logDuplicates;
        _output = output ?? Console.Out;
    }

    public long TotalCount { get; private set; }

    public long AttemptCount { get; private set; }

    public IReadOnlyDictionary<string, long> CountsByDuck => _countsByDuck;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _eventBus.SubscribeAsync(_consumerGroup, cancellationToken))
        {
            if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
            {
                continue;
            }

            AttemptCount++;

            if (!_inbox.ShouldHandle(envelope.EventId))
            {
                if (_logDuplicates)
                {
                    _output.WriteLine($"Skipping duplicate {envelope.EventId}");
                }

                continue;
            }

            var squeaked = SqueakedEnvelope.Parse(envelope);
            TotalCount++;
            _countsByDuck[squeaked.DuckId] = _countsByDuck.GetValueOrDefault(squeaked.DuckId) + 1;
            _inbox.MarkProcessed(envelope.EventId);

            if (TotalCount % _logEvery == 0)
            {
                _output.WriteLine($"[SqueakCounter] processed={TotalCount}");
            }
        }
    }
}
