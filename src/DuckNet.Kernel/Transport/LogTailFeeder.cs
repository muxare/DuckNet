using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Transport;

/// <summary>
/// Reads the event log from a start offset and publishes onto <see cref="IEventBus"/>.
/// Hostile middleware (duplicator, shuffler) wraps this publish — after the log, never before.
/// </summary>
public sealed class LogTailFeeder
{
    private readonly KernelDb _db;
    private readonly EventLogStore _log;
    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _fedOffset;

    public LogTailFeeder(KernelDb db, EventLogStore log, IEventBus eventBus, long startOffset = 0)
    {
        _db = db;
        _log = log;
        _eventBus = eventBus;
        _fedOffset = startOffset;
    }

    public long FedOffset => _fedOffset;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await FeedBatchAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CatchUpAsync(CancellationToken cancellationToken = default)
    {
        while (await FeedBatchAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    public async Task<int> FeedBatchAsync(CancellationToken cancellationToken = default, int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = _db.Read(conn => _log.ReadAfter(conn, _fedOffset, limit));
            foreach (var envelope in rows)
            {
                await _eventBus.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
                _fedOffset = envelope.LogOffset;
            }

            return rows.Count;
        }
        finally
        {
            _gate.Release();
        }
    }
}
