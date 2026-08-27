using System.Threading.Channels;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly Channel<EventEnvelope> _channel =
        Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(envelope, cancellationToken);

    public async IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = consumerGroup;

        await foreach (var envelope in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return envelope;
        }
    }
}
