using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Consumer;

public sealed class SqueakCounter
{
    private readonly IEventBus _eventBus;
    private readonly string _consumerGroup;
    private readonly Inbox _inbox;
    private readonly PerKeySequencer? _sequencer;
    private readonly ConsumerCheckpoint? _checkpoint;
    private readonly RetryPipeline? _retry;
    private readonly DeadLetterStore? _deadLetters;
    private readonly KernelDb? _db;
    private readonly EventUpcasterPipeline _upcasters;
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
        TimeSpan? gapTimeout = null,
        ConsumerCheckpoint? checkpoint = null,
        IReadOnlyDictionary<string, DuckCount>? restoredCounts = null,
        EventUpcasterPipeline? upcasters = null,
        RetryPipeline? retry = null,
        DeadLetterStore? deadLetters = null,
        KernelDb? db = null)
    {
        _eventBus = eventBus;
        _consumerGroup = consumerGroup;
        _inbox = inbox ?? new Inbox(consumerGroup);
        _sequencer = sequencerEnabled ? sequencer ?? new PerKeySequencer() : null;
        _checkpoint = checkpoint;
        _retry = retry;
        _deadLetters = deadLetters;
        _db = db;
        _upcasters = upcasters ?? EventUpcasterPipeline.Default;
        _gapTimeout = gapTimeout ?? TimeSpan.FromSeconds(5);
        _logEvery = logEvery;
        _logDuplicates = logDuplicates;
        _output = output ?? Console.Out;

        if (restoredCounts is null)
        {
            return;
        }

        foreach (var (duckId, restored) in restoredCounts)
        {
            _countsByDuck[duckId] = restored.Count;
            _lastSeqByDuck[duckId] = restored.LastSeq;
            TotalCount += restored.Count;
        }
    }

    public long TotalCount { get; private set; }

    public long AttemptCount { get; private set; }

    public long OutOfOrderCount { get; private set; }

    public long DeadLetteredCount { get; private set; }

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
        if (_retry is null || _deadLetters is null || _db is null)
        {
            HandleReadyCore(envelope);
            return;
        }

        var result = _retry.Execute(() => HandleReadyCore(envelope));
        if (result.Succeeded)
        {
            return;
        }

        DeadLetter(envelope, result);
    }

    public bool TryReplay(long id, bool fix = false)
    {
        if (_deadLetters is null || _db is null)
        {
            return false;
        }

        var row = _db.Read(conn => _deadLetters.GetById(conn, id));
        if (row is null)
        {
            return false;
        }

        var envelope = _deadLetters.EnvelopeOf(row);
        if (fix)
        {
            envelope = PoisonEvents.WithValidSqueakedPayload(envelope);
        }

        try
        {
            HandleReadyCore(envelope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _output.WriteLine($"Replay failed for DLQ {id}: {ex.Message}");
            return false;
        }

        return _db.Write((conn, tx) => _deadLetters.Delete(conn, tx, id));
    }

    public bool TrySkip(long id)
    {
        if (_deadLetters is null || _db is null)
        {
            return false;
        }

        return _db.Write((conn, tx) => _deadLetters.Delete(conn, tx, id));
    }

    private void HandleReadyCore(EventEnvelope envelope)
    {
        var squeaked = SqueakedEnvelope.Parse(_upcasters.Upcast(envelope));

        if (_checkpoint is not null)
        {
            HandleDurable(envelope, squeaked);
            return;
        }

        if (!_inbox.ShouldHandle(envelope.EventId))
        {
            LogSkip(envelope.EventId);
            return;
        }

        ApplySideEffect(squeaked);
        _inbox.MarkProcessed(envelope.EventId);
        LogProgress();
    }

    private void HandleDurable(EventEnvelope envelope, Squeaked squeaked)
    {
        var last = _lastSeqByDuck.GetValueOrDefault(squeaked.DuckId, 0);
        var applied = _checkpoint!.TryCommit(new EventEnvelopeHandle(
            envelope.EventId,
            squeaked.DuckId,
            squeaked.SequenceNumber,
            envelope.LogOffset));

        if (!applied)
        {
            LogSkip(envelope.EventId);
            return;
        }

        NoteApplied(squeaked, last);
        LogProgress();
    }

    private void ApplySideEffect(Squeaked squeaked)
    {
        var last = _lastSeqByDuck.GetValueOrDefault(squeaked.DuckId, 0);
        NoteApplied(squeaked, last);
    }

    private void NoteApplied(Squeaked squeaked, long last)
    {
        if (squeaked.SequenceNumber > last)
        {
            if (squeaked.SequenceNumber != last + 1)
            {
                OutOfOrderCount++;
                _output.WriteLine(
                    $"Out of order {squeaked.DuckId} seq {squeaked.SequenceNumber} after {last}");
            }

            _lastSeqByDuck[squeaked.DuckId] = squeaked.SequenceNumber;
        }

        TotalCount++;
        _countsByDuck[squeaked.DuckId] = _countsByDuck.GetValueOrDefault(squeaked.DuckId) + 1;
    }

    private void DeadLetter(EventEnvelope envelope, RetryResult result)
    {
        DeadLetteredCount++;
        var error = $"{result.Error!.GetType().Name}: {result.Error.Message}";
        _output.WriteLine(
            $"Dead-letter {envelope.EventId} after {result.Attempts} attempts: {result.Error.Message}");

        _db!.Write((conn, tx) =>
        {
            _deadLetters!.Insert(conn, tx, _consumerGroup, envelope, error, result.Attempts);
            if (envelope.LogOffset > 0 && _checkpoint is not null)
            {
                _checkpoint.Offsets.MarkProcessed(conn, tx, envelope.LogOffset);
            }
        });
    }

    private void LogSkip(Guid eventId)
    {
        if (_logDuplicates)
        {
            _output.WriteLine($"Skipping duplicate {eventId}");
        }
    }

    private void LogProgress()
    {
        if (TotalCount % _logEvery == 0)
        {
            _output.WriteLine($"[SqueakCounter] processed={TotalCount}");
        }
    }
}
