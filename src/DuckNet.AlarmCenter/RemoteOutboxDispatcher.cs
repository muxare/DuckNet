using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter;

/// <summary>
/// Publishes local outbox rows onto Telemetry's log via HTTP. Crash after POST
/// before mark is safe: the log ignores duplicate EventId.
/// </summary>
public sealed class RemoteOutboxDispatcher
{
    private readonly KernelDb _db;
    private readonly OutboxStore _outbox;
    private readonly HttpLogClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PollingLoop _pollingLoop;

    public RemoteOutboxDispatcher(KernelDb db, OutboxStore outbox, HttpLogClient client)
    {
        _db = db;
        _outbox = outbox;
        _client = client;
        _pollingLoop = new PollingLoop(
            DispatchAvailableAsync,
            TimeSpan.FromMilliseconds(20),
            isTransient: ex => ex is HttpRequestException);
    }

    public Task RunAsync(CancellationToken cancellationToken) => _pollingLoop.RunAsync(cancellationToken);

    public Task DrainAsync(CancellationToken cancellationToken = default) => _pollingLoop.DrainAsync(cancellationToken);

    private async Task<int> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = _db.Read(conn => _outbox.Unpublished(conn, 50));
            foreach (var row in rows)
            {
                var envelope = EnvelopeJson.Deserialize(row.PayloadJson);
                await _client.AppendAsync(envelope, cancellationToken).ConfigureAwait(false);
                _db.Write((conn, tx) =>
                    _outbox.MarkPublished(conn, tx, row.Id, DateTimeOffset.UtcNow));
            }

            return rows.Count;
        }
        finally
        {
            _gate.Release();
        }
    }
}
