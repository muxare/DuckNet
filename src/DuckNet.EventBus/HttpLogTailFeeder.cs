using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Reads Telemetry's event log over HTTP and publishes onto <see cref="IEventBus"/>.
/// Hostile middleware wraps this publish — after the log, never before.
/// </summary>
public sealed class HttpLogTailFeeder
{
    private readonly HttpLogClient _client;
    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _fedOffset;

    public HttpLogTailFeeder(HttpLogClient client, IEventBus eventBus, long startOffset = 0)
    {
        _client = client;
        _eventBus = eventBus;
        _fedOffset = startOffset;
    }

    public long FedOffset => _fedOffset;

    public async Task ResetToAsync(long offset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _fedOffset = offset;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FeedBatchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Telemetry may be restarting; retry. This is the bus, not a business call.
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpIOException)
            {
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
            var rows = await _client.ReadAfterAsync(_fedOffset, limit, cancellationToken).ConfigureAwait(false);
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
