using Microsoft.Extensions.Hosting;

namespace DuckNet.EventBus;

/// <summary>
/// Wraps a single long-running loop (feeder, consumer, dispatcher) as a hosted service,
/// swallowing the expected OperationCanceledException on shutdown.
/// </summary>
/// <remarks>
/// Register instances with <c>AddSingleton&lt;IHostedService&gt;(sp => new RunLoopHostedService(...))</c>,
/// never with the factory overload of <c>AddHostedService</c>: that overload calls
/// <c>TryAddEnumerable</c>, which dedupes descriptors by implementation type, so every
/// registration after the first RunLoopHostedService would be silently dropped.
/// </remarks>
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
