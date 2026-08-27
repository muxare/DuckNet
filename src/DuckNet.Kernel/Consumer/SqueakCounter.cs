using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Consumer;

public sealed class SqueakCounter
{
    private readonly IEventBus _eventBus;
    private readonly string _consumerGroup;
    private readonly Inbox _inbox;
    private readonly PerKeySequencer? _sequencer;
    private readonly TimeSpan _gapTimeout;
    private readonly int _logEvery;
    private readonly bool _logDuplicates;
    private readonly TextWriter _output;
    private readonly Dictionary<string, long> _countsByDuck = new();
    private readonly Dictionary<string, long> _lastSeqByDuck = new();

    public SqueakCounter(
        IEventBus eventBus,
        string consumerGroup,
        Inbox? inbox = null,
        int logEvery = 50,
        bool logDuplicates = false,
        TextWriter? output = null,
        PerKeySequencer? sequencer = null,
        bool sequencerEnabled = true,
        TimeSpan? gapTimeout = null)
    {
        _eventBus = eventBus;
        _consumerGroup = consumerGroup;
        _inbox = inbox ?? new Inbox(consumerGroup);
        _sequencer = sequencerEnabled ? sequencer ?? new PerKeySequencer() : null;
        _gapTimeout = gapTimeout ?? TimeSpan.FromSeconds(5);
        _logEvery = logEvery;
        _logDuplicates = logDuplicates;
        _output = output ?? Console.Out;
    }

    public long TotalCount { get; private set; }

    public long AttemptCount { get; private set; }

    public long OutOfOrderCount { get; private set; }

    public PerKeySequencer? Sequencer => _sequencer;

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

            foreach (var ready in Release(envelope))
            {
                HandleReady(ready);
            }

            _sequencer?.ReportGaps(_gapTimeout, _output);
        }
    }

    private IReadOnlyList<EventEnvelope> Release(EventEnvelope envelope)
    {
        if (_sequencer is null)
        {
            return [envelope];
        }

        var lateBefore = _sequencer.LateDropCount;
        var released = _sequencer.Offer(envelope);
        if (released.Count == 0 && _sequencer.LateDropCount > lateBefore && _logDuplicates)
        {
            _output.WriteLine(
                $"Dropping late seq {envelope.SequenceNumber} for {envelope.PartitionKey} (EventId={envelope.EventId})");
        }

        return released;
    }

    private void HandleReady(EventEnvelope envelope)
    {
        if (!_inbox.ShouldHandle(envelope.EventId))
        {
            if (_logDuplicates)
            {
                _output.WriteLine($"Skipping duplicate {envelope.EventId}");
            }

            return;
        }

        var squeaked = SqueakedEnvelope.Parse(envelope);
        var last = _lastSeqByDuck.GetValueOrDefault(squeaked.DuckId, 0);
        if (squeaked.SequenceNumber != last + 1)
        {
            OutOfOrderCount++;
            _output.WriteLine(
                $"Out of order {squeaked.DuckId} seq {squeaked.SequenceNumber} after {last}");
        }

        _lastSeqByDuck[squeaked.DuckId] = squeaked.SequenceNumber;
        TotalCount++;
        _countsByDuck[squeaked.DuckId] = _countsByDuck.GetValueOrDefault(squeaked.DuckId) + 1;
        _inbox.MarkProcessed(envelope.EventId);

        if (TotalCount % _logEvery == 0)
        {
            _output.WriteLine($"[SqueakCounter] processed={TotalCount}");
        }
    }
}
