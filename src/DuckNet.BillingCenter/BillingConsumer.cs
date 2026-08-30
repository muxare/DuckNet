using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;
using System.Diagnostics;

namespace DuckNet.BillingCenter;

public sealed class BillingConsumer
{
    public const string ConsumerGroup = "billing-center";

    private readonly IEventBus _eventBus;
    private readonly KernelDb _db;
    private readonly Inbox _inbox;
    private readonly ConsumerOffsetStore _offsets;
    private readonly PerKeySequencer? _sequencer;
    private readonly BillingStore _sagas;
    private readonly EventUpcasterPipeline _upcasters;
    private readonly RetryPipeline _retry;
    private readonly DeadLetterStore _deadLetters;
    private readonly TimeProvider _time;
    private readonly TextWriter _output;
    private readonly TimeSpan _gapTimeout;
    private readonly int _shardCount;
    private readonly int _shardCapacity;
    private readonly TimeSpan _handleDelay;
    private ShardWorkerPool? _pool;
    private long _handledCount;
    private long _reservedCount;
    private long _releasedCount;
    private long _attemptCount;
    private long _deadLetteredCount;

    public BillingConsumer(
        IEventBus eventBus,
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        BillingStore sagas,
        PerKeySequencer? sequencer,
        TextWriter? output = null,
        TimeSpan? gapTimeout = null,
        EventUpcasterPipeline? upcasters = null,
        RetryPipeline? retry = null,
        DeadLetterStore? deadLetters = null,
        TimeProvider? time = null,
        int shardCount = PartitionShard.DefaultCount,
        TimeSpan? handleDelay = null,
        int shardCapacity = PartitionShard.DefaultCapacity)
    {
        _eventBus = eventBus;
        _db = db;
        _inbox = inbox;
        _offsets = offsets;
        _sagas = sagas;
        _sequencer = sequencer;
        _output = output ?? Console.Out;
        _gapTimeout = gapTimeout ?? TimeSpan.FromSeconds(5);
        _upcasters = upcasters ?? EventUpcasterPipeline.Default;
        _retry = retry ?? new RetryPipeline();
        _deadLetters = deadLetters ?? new DeadLetterStore();
        _time = time ?? TimeProvider.System;
        _shardCount = shardCount < 1 ? 1 : shardCount;
        _shardCapacity = shardCapacity < 1 ? PartitionShard.DefaultCapacity : shardCapacity;
        _handleDelay = handleDelay ?? TimeSpan.Zero;
    }

    public long HandledCount => Interlocked.Read(ref _handledCount);

    public long ReservedCount => Interlocked.Read(ref _reservedCount);

    public long ReleasedCount => Interlocked.Read(ref _releasedCount);

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
        if (!IsAlarmEvent(envelope.Type))
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
        using var activity = DuckNetTracing.StartFromEnvelope(
            DuckNetTracing.Billing,
            $"handle.{envelope.Type}",
            envelope,
            consumerGroup: ConsumerGroup);

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
        var (applied, reserved, released, duckId) = _db.Write((conn, tx) =>
        {
            if (envelope.LogOffset > 0)
            {
                _offsets.MarkProcessed(conn, tx, envelope.LogOffset);
            }

            if (!_inbox.TryInsert(conn, tx, envelope.EventId))
            {
                return (false, false, false, envelope.PartitionKey);
            }

            _sagas.MarkAlarmSeq(conn, tx, envelope.PartitionKey, envelope.SequenceNumber);
            var now = _time.GetUtcNow();
            if (string.Equals(current.Type, "AlarmRaised", StringComparison.Ordinal))
            {
                var raised = AlarmRaisedEnvelope.Parse(current);
                return (true, _sagas.TryReserve(conn, tx, envelope, raised, now), false, raised.DuckId);
            }

            if (string.Equals(current.Type, "AlarmResolved", StringComparison.Ordinal))
            {
                var resolved = AlarmResolvedEnvelope.Parse(current);
                return (true, false, _sagas.TryRelease(conn, tx, envelope, resolved), resolved.DuckId);
            }

            return (true, false, false, envelope.PartitionKey);
        });

        if (!applied)
        {
            DuckNetTracing.MarkDuplicate(Activity.Current);
            return;
        }

        Interlocked.Increment(ref _handledCount);
        if (reserved)
        {
            Interlocked.Increment(ref _reservedCount);
            _output.WriteLine($"FeeReserved {duckId} alarm={envelope.EventId}");
        }
        else if (released)
        {
            Interlocked.Increment(ref _releasedCount);
            _output.WriteLine($"FeeReleased {duckId} reason={FeeReleased.ReasonAlarmResolved}");
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

    private static bool IsAlarmEvent(string type) =>
        string.Equals(type, "AlarmRaised", StringComparison.Ordinal)
        || string.Equals(type, "AlarmResolved", StringComparison.Ordinal);
}
