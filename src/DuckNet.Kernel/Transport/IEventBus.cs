namespace DuckNet.Kernel.Transport;

public interface IEventBus
{
    ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);

    IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default);
}
