using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class ShipmentDispatchedEnvelope
{
    public static EventEnvelope Create(
        ShipmentDispatched dispatched,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(dispatched.OrderId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(dispatched.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatched.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "ShipmentDispatched",
            Version: ShipmentDispatched.Version,
            PartitionKey: dispatched.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: dispatched.ShippedAt,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(dispatched, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static ShipmentDispatched Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "ShipmentDispatched", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a ShipmentDispatched envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<ShipmentDispatched>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid ShipmentDispatched payload: {envelope.EventId}");
    }
}
