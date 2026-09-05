using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.Kernel.Producer;

/// <summary>
/// Flushes the edge buffer into <c>event_log</c> only while the uplink is up.
/// Device <c>OccurredAt</c> and per-asset sequence are already on the envelope.
/// </summary>
public sealed class UplinkDispatcher
{
    private readonly KernelDb _db;
    private readonly EdgeBufferStore _buffer;
    private readonly EventLogStore _log;
    private readonly UplinkGate _uplink;
    private readonly PollingLoop _pollingLoop;

    public UplinkDispatcher(
        KernelDb db,
        EdgeBufferStore buffer,
        EventLogStore log,
        UplinkGate uplink)
    {
        _db = db;
        _buffer = buffer;
        _log = log;
        _uplink = uplink;
        _pollingLoop = new PollingLoop(FlushAvailableAsync, TimeSpan.FromMilliseconds(10));
    }

    public UplinkGate Uplink => _uplink;

    public Task RunAsync(CancellationToken cancellationToken) => _pollingLoop.RunAsync(cancellationToken);

    public Task DrainAsync(CancellationToken cancellationToken = default) => _pollingLoop.DrainAsync(cancellationToken);

    public int FlushOnce()
    {
        if (!_uplink.IsUp)
        {
            return 0;
        }

        var rows = _db.Read(conn => _buffer.Unflushed(conn, 50));
        foreach (var row in rows)
        {
            if (!_uplink.IsUp)
            {
                break;
            }

            var envelope = EnvelopeJson.Deserialize(row.PayloadJson);
            _db.Write((conn, tx) =>
            {
                _log.Append(conn, tx, envelope);
                _buffer.MarkFlushed(conn, tx, row.Id, DateTimeOffset.UtcNow);
            });
        }

        return rows.Count;
    }

    private Task<int> FlushAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FlushOnce());
    }
}
