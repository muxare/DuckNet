namespace DuckNet.AlarmCenter;

public sealed class RemoteOutboxDispatcherHostedService(RemoteOutboxDispatcher dispatcher) : BackgroundService
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
