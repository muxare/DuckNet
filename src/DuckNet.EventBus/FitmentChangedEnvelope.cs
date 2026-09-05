using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class FitmentChangedEnvelope
{
    public static EventEnvelope Create(
        FitmentChanged changed,
        Guid? eventId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changed.EquipmentModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(changed.Sku);

        return new(
            EventId: eventId ?? DeterministicEventId.For($"fitment:{changed.EquipmentModel}:{changed.Sku}"),
            Type: "FitmentChanged",
            Version: FitmentChanged.Version,
            PartitionKey: changed.EquipmentModel,
            SequenceNumber: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(changed, EnvelopeJson.Options),
            TraceId: traceId);
    }

    public static FitmentChanged Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "FitmentChanged", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a FitmentChanged envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<FitmentChanged>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid FitmentChanged payload: {envelope.EventId}");
    }
}
