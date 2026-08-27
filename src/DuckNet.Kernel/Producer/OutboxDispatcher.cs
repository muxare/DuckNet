using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Producer;

/// <summary>
/// Moves unpublished outbox rows into the append-only event log, then marks
/// them published — one transaction per row so a crash cannot dual-write.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly KernelDb _db;
    private readonly OutboxStore _outbox;
    private readonly EventLogStore _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxDispatcher(KernelDb db, OutboxStore outbox, EventLogStore log)
    {
        _db = db;
        _outbox = outbox;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (await DispatchAvailableAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    private async Task<int> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return DispatchBatch();
        }
        finally
        {
            _gate.Release();
        }
    }

    private int DispatchBatch()
    {
        var rows = _db.Read(conn => _outbox.Unpublished(conn, 50));
        foreach (var row in rows)
        {
            var envelope = EnvelopeJson.Deserialize(row.PayloadJson);
            _db.Write((conn, tx) =>
            {
                _log.Append(conn, tx, envelope);
                _outbox.MarkPublished(conn, tx, row.Id, DateTimeOffset.UtcNow);
            });
        }

        return rows.Count;
    }
}
