using DuckNet.Kernel.Producer;

namespace DuckNet.TelemetryCenter;

public sealed class SimulatorHostedService(DuckSimulator simulator) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await simulator.RunAsync(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
