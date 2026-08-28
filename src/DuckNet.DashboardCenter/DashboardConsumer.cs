using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter;

public sealed class DashboardConsumer
{
    public const string ConsumerGroup = "dashboard-projector";

    private readonly IEventBus _eventBus;
    private readonly KernelDb _db;
    private readonly Inbox _inbox;
    private readonly ConsumerOffsetStore _offsets;
    private readonly DashboardReadModel _readModel;
    private readonly EventUpcasterPipeline _upcasters;
    private readonly RetryPipeline _retry;
    private readonly DeadLetterStore _deadLetters;
    private readonly HttpLogTailFeeder? _feeder;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly TextWriter _output;
    private readonly int _shardCount;
    private readonly int _shardCapacity;
    private readonly TimeSpan _handleDelay;
    private ShardWorkerPool? _pool;
    private long _handledCount;
    private long _attemptCount;
    private long _deadLetteredCount;

    public DashboardConsumer(
        IEventBus eventBus,
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        DashboardReadModel readModel,
        HttpLogTailFeeder? feeder = null,
        TextWriter? output = null,
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
        _readModel = readModel;
        _feeder = feeder;
        _output = output ?? Console.Out;
        _upcasters = upcasters ?? EventUpcasterPipeline.Default;
        _retry = retry ?? new RetryPipeline();
        _deadLetters = deadLetters ?? new DeadLetterStore();
        _shardCount = shardCount < 1 ? 1 : shardCount;
        _shardCapacity = shardCapacity < 1 ? PartitionShard.DefaultCapacity : shardCapacity;
        _handleDelay = handleDelay ?? TimeSpan.Zero;
    }

    public long HandledCount => Interlocked.Read(ref _handledCount);

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
                Handle(envelope);
                return Task.CompletedTask;
            },
            _shardCapacity);
        _pool = pool;

        try
        {
            await foreach (var envelope in _eventBus.SubscribeAsync(ConsumerGroup, cancellationToken))
            {
                await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await pool.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _rebuildGate.Release();
                }
            }
        }
        finally
        {
            await pool.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pool is not null)
            {
                await _pool.DrainAsync(cancellationToken).ConfigureAwait(false);
            }

            _db.Write((conn, tx) =>
            {
                _readModel.Truncate(conn, tx);
                _inbox.Clear(conn, tx);
                _offsets.Reset(conn, tx);
            });
            Interlocked.Exchange(ref _handledCount, 0);
            Interlocked.Exchange(ref _attemptCount, 0);
            Interlocked.Exchange(ref _deadLetteredCount, 0);
            _pool?.Metrics.Reset();

            if (_feeder is not null)
            {
                await _feeder.ResetToAsync(0, cancellationToken).ConfigureAwait(false);
            }

            _output.WriteLine("Dashboard rebuild: truncated read model, replaying from offset 0");
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private void Handle(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
        {
            AdvanceOffset(envelope.LogOffset);
            return;
        }

        Interlocked.Increment(ref _attemptCount);
        if (_handleDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_handleDelay);
        }

        var result = _retry.Execute(() => HandleCore(envelope));
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
            HandleCore(envelope);
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

    private void HandleCore(EventEnvelope envelope)
    {
        var current = _upcasters.Upcast(envelope);
        var squeaked = SqueakedEnvelope.Parse(current);
        var applied = _db.Write((conn, tx) =>
        {
            if (envelope.LogOffset > 0)
            {
                _offsets.MarkProcessed(conn, tx, envelope.LogOffset);
            }

            if (!_inbox.TryInsert(conn, tx, envelope.EventId))
            {
                return false;
            }

            _readModel.ApplySqueak(conn, tx, squeaked.DuckId, squeaked.OccurredAt, squeaked.VolumeDb);
            return true;
        });

        if (applied)
        {
            Interlocked.Increment(ref _handledCount);
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
