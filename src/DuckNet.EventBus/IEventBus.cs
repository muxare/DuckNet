using DuckNet.Contracts;

namespace DuckNet.EventBus;

public interface IEventBus
{
    ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);

    IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default);
}
