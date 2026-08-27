using DuckNet.Kernel.Producer;

namespace DuckNet.TelemetryCenter;

public sealed class OutboxDispatcherHostedService(OutboxDispatcher dispatcher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await dispatcher.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
