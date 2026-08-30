using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DuckNet.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DuckNet.EventBus;

/// <summary>
/// Broker-backed <see cref="IEventBus"/>. Topic exchange <c>ducknet.events</c>
/// (or <c>DUCKNET_BUS_EXCHANGE</c>); one durable queue per consumer group.
/// Routing key is <c>{type}.{version}</c>. Manual ack after the subscriber
/// requests the next envelope (or dispose). Inbox — not this type — is the
/// dedupe. Automatic recovery covers broker restarts.
/// </summary>
public sealed class RabbitMqEventBus : IEventBus, IAsyncDisposable
{
    public const string DefaultExchange = EventBusFactory.DefaultExchange;

    private readonly string _connectionString;
    private readonly string _exchange;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private bool _disposed;

    public RabbitMqEventBus(string connectionString, string? exchange = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _exchange = string.IsNullOrWhiteSpace(exchange) ? DefaultExchange : exchange;
    }

    public async ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var routingKey = RoutingKey(envelope);
        var body = Encoding.UTF8.GetBytes(EnvelopeJson.Serialize(envelope));
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = envelope.EventId.ToString(),
            Type = envelope.Type
        };

        var delay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var channel = _publishChannel
                        ?? throw new InvalidOperationException("RabbitMQ publish channel is not open.");
                    await channel.BasicPublishAsync(
                            _exchange,
                            routingKey,
                            mandatory: false,
                            properties,
                            body,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                finally
                {
                    _publishGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (!_disposed)
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }
    }

    public IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        string consumerGroup,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        var deliveries = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = ConsumeLoopAsync(consumerGroup, deliveries.Writer, cancellationToken);
        return ReadDeliveriesAsync(deliveries.Reader, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ResetConnectionAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _publishGate.Dispose();
    }

    internal static string QueueName(string exchange, string consumerGroup) =>
        $"ducknet.{exchange}.{consumerGroup}";

    internal static string RoutingKey(EventEnvelope envelope) =>
        $"{envelope.Type}.{envelope.Version}";

    private async Task ConsumeLoopAsync(
        string consumerGroup,
        ChannelWriter<Delivery> writer,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            IChannel? channel = null;
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var connection = _connection
                    ?? throw new InvalidOperationException("RabbitMQ connection is not open.");
                channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await DeclareTopologyAsync(channel, consumerGroup, cancellationToken).ConfigureAwait(false);
                await channel.BasicQosAsync(0, prefetchCount: 32, global: false, cancellationToken)
                    .ConfigureAwait(false);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, args) =>
                {
                    var json = Encoding.UTF8.GetString(args.Body.ToArray());
                    var envelope = EnvelopeJson.Deserialize(json);
                    writer.TryWrite(new Delivery(args.DeliveryTag, envelope, channel));
                    return Task.CompletedTask;
                };

                var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) =>
                {
                    lost.TrySetResult();
                    return Task.CompletedTask;
                };
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    lost.TrySetResult();
                    return Task.CompletedTask;
                };

                await channel.BasicConsumeAsync(
                        QueueName(_exchange, consumerGroup),
                        autoAck: false,
                        consumer,
                        cancellationToken)
                    .ConfigureAwait(false);

                delay = TimeSpan.FromMilliseconds(200);
                await Task.WhenAny(lost.Task, Task.Delay(Timeout.Infinite, cancellationToken))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch when (!_disposed)
            {
                if (channel is not null)
                {
                    await TryCloseAsync(channel).ConfigureAwait(false);
                }

                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2000));
            }
        }

        writer.TryComplete();
    }

    private static async IAsyncEnumerable<EventEnvelope> ReadDeliveriesAsync(
        ChannelReader<Delivery> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong? pendingTag = null;
        IChannel? pendingChannel = null;
        try
        {
            await foreach (var delivery in reader.ReadAllAsync(cancellationToken))
            {
                if (pendingTag is ulong tag && pendingChannel is not null)
                {
                    await TryAckAsync(pendingChannel, tag).ConfigureAwait(false);
                }

                pendingTag = delivery.Tag;
                pendingChannel = delivery.Channel;
                yield return delivery.Envelope;
            }
        }
        finally
        {
            if (pendingTag is ulong leftover && pendingChannel is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await TryNackAsync(pendingChannel, leftover).ConfigureAwait(false);
                }
                else
                {
                    await TryAckAsync(pendingChannel, leftover).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _publishChannel is { IsOpen: true })
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } && _publishChannel is { IsOpen: true })
            {
                return;
            }

            await ResetConnectionAsync().ConfigureAwait(false);
            var factory = CreateFactory(_connectionString);
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _publishChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _publishChannel.ExchangeDeclareAsync(
                    _exchange,
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DeclareTopologyAsync(
        IChannel channel,
        string consumerGroup,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
                _exchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var queue = QueueName(_exchange, consumerGroup);
        await channel.QueueDeclareAsync(
                queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await channel.QueueBindAsync(
                queue,
                _exchange,
                routingKey: "#",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResetConnectionAsync()
    {
        if (_publishChannel is not null)
        {
            await TryCloseAsync(_publishChannel).ConfigureAwait(false);
            _publishChannel = null;
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Closing a broken connection is best-effort.
            }

            try
            {
                _connection.Dispose();
            }
            catch
            {
                // Ignore dispose races during recovery.
            }

            _connection = null;
        }
    }

    private static ConnectionFactory CreateFactory(string connectionString)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(1),
            RequestedHeartbeat = TimeSpan.FromSeconds(10),
            ClientProvidedName = "DuckNet"
        };

        factory.Uri = new Uri(connectionString);
        return factory;
    }

    private static async Task TryAckAsync(IChannel channel, ulong tag)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.BasicAckAsync(tag, multiple: false).ConfigureAwait(false);
            }
        }
        catch
        {
            // Redelivery + inbox covers a lost ack.
        }
    }

    private static async Task TryNackAsync(IChannel channel, ulong tag)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.BasicNackAsync(tag, multiple: false, requeue: true).ConfigureAwait(false);
            }
        }
        catch
        {
            // Broker will redeliver when the channel drops.
        }
    }

    private static async Task TryCloseAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            channel.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    private readonly record struct Delivery(ulong Tag, EventEnvelope Envelope, IChannel Channel);
}
