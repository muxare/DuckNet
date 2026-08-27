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

    public RemoteOutboxDispatcher(KernelDb db, OutboxStore outbox, HttpLogClient client)
    {
        _db = db;
        _outbox = outbox;
        _client = client;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
