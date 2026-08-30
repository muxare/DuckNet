using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class FeeReservedEnvelope
{
    public static EventEnvelope Create(
        FeeReserved reserved,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reserved.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reserved.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(reserved.AmountCents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "FeeReserved",
            Version: FeeReserved.Version,
            PartitionKey: reserved.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(reserved, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static FeeReserved Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "FeeReserved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a FeeReserved envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<FeeReserved>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid FeeReserved payload: {envelope.EventId}");
    }
}
