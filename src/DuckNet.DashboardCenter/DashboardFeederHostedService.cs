using DuckNet.EventBus;

namespace DuckNet.DashboardCenter;

public sealed class DashboardFeederHostedService(HttpLogTailFeeder feeder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await feeder.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
