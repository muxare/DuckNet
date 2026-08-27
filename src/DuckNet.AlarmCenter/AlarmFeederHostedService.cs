using DuckNet.EventBus;

namespace DuckNet.AlarmCenter;

public sealed class AlarmFeederHostedService(HttpLogTailFeeder feeder) : BackgroundService
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
