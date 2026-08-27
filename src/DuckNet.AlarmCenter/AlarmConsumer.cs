using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter;

public sealed class AlarmConsumer
{
    public const string ConsumerGroup = "alarm-center";

    private readonly IEventBus _eventBus;
    private readonly KernelDb _db;
    private readonly Inbox _inbox;
    private readonly ConsumerOffsetStore _offsets;
    private readonly PerKeySequencer? _sequencer;
    private readonly AlarmStore _alarms;
    private readonly TextWriter _output;
    private readonly TimeSpan _gapTimeout;

    public AlarmConsumer(
        IEventBus eventBus,
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        AlarmStore alarms,
        PerKeySequencer? sequencer,
        TextWriter? output = null,
        TimeSpan? gapTimeout = null)
    {
        _eventBus = eventBus;
        _db = db;
        _inbox = inbox;
        _offsets = offsets;
        _alarms = alarms;
        _sequencer = sequencer;
        _output = output ?? Console.Out;
        _gapTimeout = gapTimeout ?? TimeSpan.FromSeconds(5);
    }

    public long HandledCount { get; private set; }

    public long RaisedCount { get; private set; }

    public long AttemptCount { get; private set; }

    public ConsumerOffsetStore Offsets => _offsets;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _eventBus.SubscribeAsync(ConsumerGroup, cancellationToken))
        {
            if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
            {
                AdvanceOffset(envelope.LogOffset);
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

        return _sequencer.Offer(envelope);
    }

    private void HandleReady(EventEnvelope envelope)
    {
        var squeaked = SqueakedEnvelope.Parse(envelope);
        var (applied, raised) = _db.Write((conn, tx) =>
        {
            if (envelope.LogOffset > 0)
            {
                _offsets.MarkProcessed(conn, tx, envelope.LogOffset);
            }

            if (!_inbox.TryInsert(conn, tx, envelope.EventId))
            {
                return (false, false);
            }

            _alarms.MarkSqueakSeq(conn, tx, squeaked.DuckId, squeaked.SequenceNumber);
            var raisedNow = _alarms.TryRaise(conn, tx, envelope, squeaked);
            return (true, raisedNow);
        });

        if (!applied)
        {
            return;
        }

        HandledCount++;
        if (raised)
        {
            RaisedCount++;
            _output.WriteLine(
                $"AlarmRaised {squeaked.DuckId} after {HandledCount} unique squeaks (EventId={envelope.EventId})");
        }
    }

    private void AdvanceOffset(long logOffset)
    {
        if (logOffset <= 0)
        {
            return;
        }

        _db.Write((conn, tx) => _offsets.MarkProcessed(conn, tx, logOffset));
    }
}
