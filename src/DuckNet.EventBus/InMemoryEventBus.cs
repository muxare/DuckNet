using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Process-local bus. Each <c>consumerGroup</c> has its own channel so two
/// groups each get a copy of every publish (fan-out). Same group = competing
/// consumers on one channel. Inbox — not this type — is the dedupe.
/// Late subscribers receive already-published envelopes (in-memory backlog),
/// which keeps kernel tests green when <c>RunAsync</c> races <c>PublishAsync</c>.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly List<EventEnvelope> _history = [];
    private readonly Dictionary<string, Channel<EventEnvelope>> _groups = new(StringComparer.Ordinal);

    public ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _history.Add(envelope);
            foreach (var channel in _groups.Values)
            {
                channel.Writer.TryWrite(envelope);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        var channel = GetOrAddGroup(consumerGroup);
        return ReadAllAsync(channel.Reader, cancellationToken);
    }

    private Channel<EventEnvelope> GetOrAddGroup(string consumerGroup)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(consumerGroup, out var existing))
            {
                return existing;
            }

            var channel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });

            foreach (var envelope in _history)
            {
                channel.Writer.TryWrite(envelope);
            }

            _groups[consumerGroup] = channel;
            return channel;
        }
    }

    private static async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        ChannelReader<EventEnvelope> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in reader.ReadAllAsync(cancellationToken))
        {
            yield return envelope;
        }
    }
}
