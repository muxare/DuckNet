using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
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
    private readonly EventUpcasterPipeline _upcasters;
    private readonly RetryPipeline _retry;
    private readonly DeadLetterStore _deadLetters;
    private readonly TextWriter _output;
    private readonly TimeSpan _gapTimeout;
    private readonly int _shardCount;
    private readonly int _shardCapacity;
    private readonly TimeSpan _handleDelay;
    private ShardWorkerPool? _pool;
    private long _handledCount;
    private long _raisedCount;
    private long _attemptCount;
    private long _deadLetteredCount;

    public AlarmConsumer(
        IEventBus eventBus,
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        AlarmStore alarms,
        PerKeySequencer? sequencer,
        TextWriter? output = null,
        TimeSpan? gapTimeout = null,
        EventUpcasterPipeline? upcasters = null,
        RetryPipeline? retry = null,
        DeadLetterStore? deadLetters = null,
        int shardCount = PartitionShard.DefaultCount,
        TimeSpan? handleDelay = null,
        int shardCapacity = PartitionShard.DefaultCapacity)
    {
        _eventBus = eventBus;
        _db = db;
        _inbox = inbox;
        _offsets = offsets;
        _alarms = alarms;
        _sequencer = sequencer;
        _output = output ?? Console.Out;
        _gapTimeout = gapTimeout ?? TimeSpan.FromSeconds(5);
        _upcasters = upcasters ?? EventUpcasterPipeline.Default;
        _retry = retry ?? new RetryPipeline();
        _deadLetters = deadLetters ?? new DeadLetterStore();
        _shardCount = shardCount < 1 ? 1 : shardCount;
        _shardCapacity = shardCapacity < 1 ? PartitionShard.DefaultCapacity : shardCapacity;
        _handleDelay = handleDelay ?? TimeSpan.Zero;
    }

    public long HandledCount => Interlocked.Read(ref _handledCount);

    public long RaisedCount => Interlocked.Read(ref _raisedCount);

    public long AttemptCount => Interlocked.Read(ref _attemptCount);

    public long DeadLetteredCount => Interlocked.Read(ref _deadLetteredCount);

    public ConsumerOffsetStore Offsets => _offsets;

    public ShardMetricsSnapshot? ShardSnapshot => _pool?.Snapshot();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = new ShardWorkerPool(
            _shardCount,
            (envelope, _) =>
            {
                HandleDispatched(envelope);
                return Task.CompletedTask;
            },
            _shardCapacity);
        _pool = pool;

        try
        {
            await foreach (var envelope in _eventBus.SubscribeAsync(ConsumerGroup, cancellationToken))
            {
                await pool.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await pool.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task DrainAsync(CancellationToken cancellationToken = default) =>
        _pool?.DrainAsync(cancellationToken) ?? Task.CompletedTask;

    private void HandleDispatched(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
        {
            AdvanceOffset(envelope.LogOffset);
            return;
        }

        Interlocked.Increment(ref _attemptCount);
        foreach (var ready in Release(envelope))
        {
            HandleReady(ready);
        }

        _sequencer?.ReportGaps(_gapTimeout, _output);
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
        if (_handleDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_handleDelay);
        }

        var result = _retry.Execute(() => HandleReadyCore(envelope));
        if (!result.Succeeded)
        {
            DeadLetter(envelope, result);
        }
    }

    public bool TryReplay(long id, bool fix = false)
    {
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

    public bool TrySkip(long id) =>
        _db.Write((conn, tx) => _deadLetters.Delete(conn, tx, id));

    private void HandleReadyCore(EventEnvelope envelope)
    {
        var current = _upcasters.Upcast(envelope);
        var squeaked = SqueakedEnvelope.Parse(current);
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

        Interlocked.Increment(ref _handledCount);
        if (raised)
        {
            Interlocked.Increment(ref _raisedCount);
            _output.WriteLine(
                $"AlarmRaised {squeaked.DuckId} after {HandledCount} unique squeaks (EventId={envelope.EventId})");
        }
    }

    private void DeadLetter(EventEnvelope envelope, RetryResult result)
    {
        Interlocked.Increment(ref _deadLetteredCount);
        var error = $"{result.Error!.GetType().Name}: {result.Error.Message}";
        _output.WriteLine(
            $"Dead-letter {envelope.EventId} after {result.Attempts} attempts: {result.Error.Message}");

        _db.Write((conn, tx) =>
        {
            _deadLetters.Insert(conn, tx, ConsumerGroup, envelope, error, result.Attempts);
            if (envelope.LogOffset > 0)
            {
                _offsets.MarkProcessed(conn, tx, envelope.LogOffset);
            }
        });
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
