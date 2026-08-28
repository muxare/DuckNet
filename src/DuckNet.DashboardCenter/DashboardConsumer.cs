using DuckNet.Contracts;
using DuckNet.EventBus;
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
    private readonly HttpLogTailFeeder? _feeder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TextWriter _output;

    public DashboardConsumer(
        IEventBus eventBus,
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        DashboardReadModel readModel,
        HttpLogTailFeeder? feeder = null,
        TextWriter? output = null)
    {
        _eventBus = eventBus;
        _db = db;
        _inbox = inbox;
        _offsets = offsets;
        _readModel = readModel;
        _feeder = feeder;
        _output = output ?? Console.Out;
    }

    public long HandledCount { get; private set; }

    public long AttemptCount { get; private set; }

    public ConsumerOffsetStore Offsets => _offsets;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _eventBus.SubscribeAsync(ConsumerGroup, cancellationToken))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Handle(envelope);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _db.Write((conn, tx) =>
            {
                _readModel.Truncate(conn, tx);
                _inbox.Clear(conn, tx);
                _offsets.Reset(conn, tx);
            });
            HandledCount = 0;
            AttemptCount = 0;

            if (_feeder is not null)
            {
                await _feeder.ResetToAsync(0, cancellationToken).ConfigureAwait(false);
            }

            _output.WriteLine("Dashboard rebuild: truncated read model, replaying from offset 0");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Handle(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
        {
            AdvanceOffset(envelope.LogOffset);
            return;
        }

        AttemptCount++;
        var squeaked = SqueakedEnvelope.Parse(envelope);
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

            _readModel.ApplySqueak(conn, tx, squeaked.DuckId, squeaked.OccurredAt);
            return true;
        });

        if (applied)
        {
            HandledCount++;
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
