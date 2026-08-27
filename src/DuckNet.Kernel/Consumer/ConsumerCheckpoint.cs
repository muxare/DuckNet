using DuckNet.Kernel.Persistence;
using Microsoft.Data.Sqlite;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Inbox + offset + counts in one transaction. Crash before commit retries
/// the same log offsets; crash after commit skips them on restart.
/// </summary>
public sealed class ConsumerCheckpoint
{
    private readonly KernelDb _db;
    private readonly Inbox _inbox;
    private readonly ConsumerOffsetStore _offsets;
    private readonly SqueakCountStore _counts;
    private readonly string _consumerGroup;

    public ConsumerCheckpoint(
        KernelDb db,
        Inbox inbox,
        ConsumerOffsetStore offsets,
        SqueakCountStore counts)
    {
        _db = db;
        _inbox = inbox;
        _offsets = offsets;
        _counts = counts;
        _consumerGroup = inbox.ConsumerGroup;
    }

    public ConsumerOffsetStore Offsets => _offsets;

    public bool TryCommit(EventEnvelopeHandle handle)
    {
        return _db.Write((conn, tx) =>
        {
            var isNew = _inbox.TryInsert(conn, tx, handle.EventId);
            if (handle.LogOffset > 0)
            {
                _offsets.MarkProcessed(conn, tx, handle.LogOffset);
            }

            if (!isNew)
            {
                return false;
            }

            _counts.Increment(conn, tx, _consumerGroup, handle.DuckId, handle.SequenceNumber);
            return true;
        });
    }
}

public readonly record struct EventEnvelopeHandle(
    Guid EventId,
    string DuckId,
    long SequenceNumber,
    long LogOffset);
