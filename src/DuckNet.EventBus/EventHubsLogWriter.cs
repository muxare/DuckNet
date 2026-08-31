using System.Text;
using Azure.Core;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Azure Event Hubs append for the telemetry log (system of record / replay).
/// Partition key is the envelope <c>PartitionKey</c> (<c>duckId</c>). Local
/// Aspire still writes SQLite <c>event_log</c>; this type is selected by env
/// in 12c. Inbox / sequencer stay on the consumer.
/// </summary>
public sealed class EventHubsLogWriter : IAsyncDisposable
{
    public const string DefaultHub = "ducknet-events";

    private readonly EventHubProducerClient _producer;

    public EventHubsLogWriter(string connectionString, string? hubName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var hub = string.IsNullOrWhiteSpace(hubName) ? DefaultHub : hubName;
        _producer = new EventHubProducerClient(connectionString, hub);
    }

    public EventHubsLogWriter(string fullyQualifiedNamespace, string hubName, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(hubName);
        ArgumentNullException.ThrowIfNull(credential);
        _producer = new EventHubProducerClient(fullyQualifiedNamespace, hubName, credential);
    }

    public static string PartitionKeyFor(EventEnvelope envelope) => envelope.PartitionKey;

    public async Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var data = new EventData(Encoding.UTF8.GetBytes(EnvelopeJson.Serialize(envelope)))
        {
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CausationId,
            ContentType = "application/json"
        };
        data.Properties["type"] = envelope.Type;
        data.Properties["version"] = envelope.Version;

        var options = new SendEventOptions { PartitionKey = PartitionKeyFor(envelope) };
        await _producer.SendAsync([data], options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _producer.DisposeAsync();
}

/// <summary>
/// Picks <see cref="EventHubsLogWriter"/> when Event Hubs env is set; otherwise
/// null so Telemetry keeps SQLite <c>event_log</c>.
/// </summary>
public static class EventHubsLogWriterFactory
{
    public static EventHubsLogWriter? TryCreate()
    {
        var hub = EventBusFactory.FirstNonEmpty(
            Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_HUB"))
            ?? EventHubsLogWriter.DefaultHub;

        var connection = EventBusFactory.FirstNonEmpty(
            Environment.GetEnvironmentVariable("ConnectionStrings__eventhubs"),
            Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_CONNECTION"));
        if (!string.IsNullOrWhiteSpace(connection))
        {
            return new EventHubsLogWriter(connection, hub);
        }

        var ns = EventBusFactory.FirstNonEmpty(
            Environment.GetEnvironmentVariable("DUCKNET_EVENTHUBS_NAMESPACE"));
        if (!string.IsNullOrWhiteSpace(ns))
        {
            return new EventHubsLogWriter(ns, hub, new Azure.Identity.DefaultAzureCredential());
        }

        return null;
    }
}
