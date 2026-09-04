using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class OrderConfirmedEnvelope
{
    public static EventEnvelope Create(
        OrderConfirmed confirmed,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(confirmed.OrderId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(confirmed.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmed.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "OrderConfirmed",
            Version: OrderConfirmed.Version,
            PartitionKey: confirmed.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: confirmed.ConfirmedAt,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(confirmed, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static OrderConfirmed Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "OrderConfirmed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not an OrderConfirmed envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<OrderConfirmed>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid OrderConfirmed payload: {envelope.EventId}");
    }
}
