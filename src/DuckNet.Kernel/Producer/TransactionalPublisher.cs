using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Producer;

/// <summary>
/// Writes duck seq and the outbox row in one transaction. The bus is not
/// touched here — <see cref="OutboxDispatcher"/> appends the log, then the
/// tail feeder publishes through hostile middleware.
/// </summary>
public sealed class TransactionalPublisher
{
    private readonly KernelDb _db;
    private readonly StateStore _state;
    private readonly OutboxStore _outbox;

    public TransactionalPublisher(KernelDb db, StateStore state, OutboxStore outbox)
    {
        _db = db;
        _state = state;
        _outbox = outbox;
    }

    public Task PublishSqueakAsync(string duckId, CancellationToken cancellationToken = default) =>
        PublishSqueakAsync(duckId, volumeDb: 60, cancellationToken);

    public Task PublishSqueakAsync(string duckId, double volumeDb, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(duckId);

        _db.Write((conn, tx) =>
        {
            var sequence = _state.NextSequence(conn, tx, duckId);
            var squeaked = new Squeaked(duckId, sequence, DateTimeOffset.UtcNow, volumeDb);
            // PartitionKey = duck id. Sequence is per duck, never global. Envelope version is v2.
            _outbox.Insert(conn, tx, SqueakedEnvelope.Create(squeaked));
        });

        return Task.CompletedTask;
    }
}
