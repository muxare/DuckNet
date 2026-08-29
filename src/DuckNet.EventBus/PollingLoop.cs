namespace DuckNet.EventBus;

/// <summary>
/// Generic gate + fetch-batch + poll-loop scaffold shared by feeders and outbox
/// dispatchers: repeatedly runs a batch step, waits between polls, and can be
/// drained (or caught up) by running the batch step until it reports zero.
/// The batch step owns its own concurrency gate; this loop only sequences it.
/// </summary>
public sealed class PollingLoop
{
    private readonly Func<CancellationToken, Task<int>> _batchStep;
    private readonly TimeSpan _delay;
    private readonly Func<Exception, bool>? _isTransient;

    public PollingLoop(Func<CancellationToken, Task<int>> batchStep, TimeSpan delay, Func<Exception, bool>? isTransient = null)
    {
        _batchStep = batchStep;
        _delay = delay;
        _isTransient = isTransient;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _batchStep(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (_isTransient is not null && _isTransient(ex))
            {
                // Transport hiccup; the bus is retried on the next poll, not here.
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (await _batchStep(cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    public Task CatchUpAsync(CancellationToken cancellationToken = default) => DrainAsync(cancellationToken);
}
