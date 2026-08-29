namespace DuckNet.EventBus;

/// <summary>
/// Wraps a single long-running loop (feeder, consumer, dispatcher) as a hosted service,
/// swallowing the expected OperationCanceledException on shutdown.
/// </summary>
public sealed class RunLoopHostedService(Func<CancellationToken, Task> run) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await run(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
