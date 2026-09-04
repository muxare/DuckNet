namespace DuckNet.Kernel.Producer;

public interface ITelemetrySimulator
{
    Task RunAsync(TimeSpan duration, CancellationToken cancellationToken);
}
