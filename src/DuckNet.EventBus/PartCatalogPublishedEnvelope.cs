using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class PartCatalogPublishedEnvelope
{
    public static EventEnvelope Create(
        PartCatalogPublished published,
        Guid? eventId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(published.Sku);

        return new(
            EventId: eventId ?? DeterministicEventId.For("catalog-part:" + published.Sku),
            Type: "PartCatalogPublished",
            Version: PartCatalogPublished.Version,
            PartitionKey: published.Sku,
            SequenceNumber: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(published, EnvelopeJson.Options),
            TraceId: traceId);
    }

    public static PartCatalogPublished Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "PartCatalogPublished", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a PartCatalogPublished envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<PartCatalogPublished>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid PartCatalogPublished payload: {envelope.EventId}");
    }
}
