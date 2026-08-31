using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Azure Service Bus <see cref="IEventBus"/>. Topic <c>ducknet-events</c>
/// (or <c>DUCKNET_BUS_TOPIC</c>); one subscription per consumer group.
/// At-least-once: complete after the subscriber requests the next envelope
/// (or dispose); abandon on cancel → redelivery. Inbox — not this type — is
/// the dedupe. Topology (topic + subscriptions) is created when the
/// connection string has Manage; Bicep owns it in Azure.
/// </summary>
public sealed class ServiceBusEventBus : IEventBus, IAsyncDisposable
{
    public const string DefaultTopic = "ducknet-events";

    private readonly string _topic;
    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient? _admin;
    private readonly ServiceBusSender _sender;
    private bool _disposed;

    public ServiceBusEventBus(string connectionString, string? topic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _topic = string.IsNullOrWhiteSpace(topic) ? DefaultTopic : topic;
        _client = new ServiceBusClient(connectionString);
        _admin = new ServiceBusAdministrationClient(connectionString);
        _sender = _client.CreateSender(_topic);
    }

    public ServiceBusEventBus(string fullyQualifiedNamespace, TokenCredential credential, string? topic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        _topic = string.IsNullOrWhiteSpace(topic) ? DefaultTopic : topic;
        _client = new ServiceBusClient(fullyQualifiedNamespace, credential);
        _admin = new ServiceBusAdministrationClient(fullyQualifiedNamespace, credential);
        _sender = _client.CreateSender(_topic);
    }

    public async ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(EnvelopeJson.Serialize(envelope));
        var message = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = envelope.EventId.ToString(),
            Subject = envelope.Type
        };
        message.ApplicationProperties["type"] = envelope.Type;
        message.ApplicationProperties["version"] = envelope.Version;
        message.ApplicationProperties["partitionKey"] = envelope.PartitionKey;

        var delay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureTopicAsync(cancellationToken).ConfigureAwait(false);
                await _sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                await _sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch when (!_disposed)
            {
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
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    internal static string SubscriptionName(string consumerGroup) => consumerGroup;

    private async Task ConsumeLoopAsync(
        string consumerGroup,
        ChannelWriter<Delivery> writer,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            ServiceBusReceiver? receiver = null;
            try
            {
                await EnsureSubscriptionAsync(consumerGroup, cancellationToken).ConfigureAwait(false);
                receiver = _client.CreateReceiver(_topic, SubscriptionName(consumerGroup), new ServiceBusReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                    PrefetchCount = 32
                });

                delay = TimeSpan.FromMilliseconds(200);
                while (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    var message = await receiver
                        .ReceiveMessageAsync(TimeSpan.FromSeconds(5), cancellationToken)
                        .ConfigureAwait(false);
                    if (message is null)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(message.Body.ToArray());
                    var envelope = EnvelopeJson.Deserialize(json);
                    await writer.WriteAsync(new Delivery(envelope, message, receiver), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch when (!_disposed)
            {
                if (receiver is not null)
                {
                    await receiver.DisposeAsync().ConfigureAwait(false);
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
        ServiceBusReceivedMessage? pending = null;
        ServiceBusReceiver? pendingReceiver = null;
        try
        {
            await foreach (var delivery in reader.ReadAllAsync(cancellationToken))
            {
                if (pending is not null && pendingReceiver is not null)
                {
                    await TryCompleteAsync(pendingReceiver, pending).ConfigureAwait(false);
                }

                pending = delivery.Message;
                pendingReceiver = delivery.Receiver;
                yield return delivery.Envelope;
            }
        }
        finally
        {
            if (pending is not null && pendingReceiver is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await TryAbandonAsync(pendingReceiver, pending).ConfigureAwait(false);
                }
                else
                {
                    await TryCompleteAsync(pendingReceiver, pending).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task EnsureTopicAsync(CancellationToken cancellationToken)
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            if (!await _admin.TopicExistsAsync(_topic, cancellationToken).ConfigureAwait(false))
            {
                await _admin.CreateTopicAsync(_topic, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Bicep owns topology; data-plane identity cannot Manage.
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Race with another publisher.
        }
    }

    private async Task EnsureSubscriptionAsync(string consumerGroup, CancellationToken cancellationToken)
    {
        await EnsureTopicAsync(cancellationToken).ConfigureAwait(false);
        if (_admin is null)
        {
            return;
        }

        var name = SubscriptionName(consumerGroup);
        try
        {
            if (!await _admin.SubscriptionExistsAsync(_topic, name, cancellationToken).ConfigureAwait(false))
            {
                await _admin.CreateSubscriptionAsync(
                        new CreateSubscriptionOptions(_topic, name)
                        {
                            MaxDeliveryCount = 10,
                            DeadLetteringOnMessageExpiration = true,
                            LockDuration = TimeSpan.FromMinutes(1)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Bicep owns topology.
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Race with another subscriber.
        }
    }

    private static async Task TryCompleteAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.CompleteMessageAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Redelivery + inbox covers a lost complete.
        }
    }

    private static async Task TryAbandonAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Lock expiry redelivers.
        }
    }

    private readonly record struct Delivery(
        EventEnvelope Envelope,
        ServiceBusReceivedMessage Message,
        ServiceBusReceiver Receiver);
}
