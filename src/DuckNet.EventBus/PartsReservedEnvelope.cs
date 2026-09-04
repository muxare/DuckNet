using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class PartsReservedEnvelope
{
    public static EventEnvelope Create(
        PartsReserved reserved,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reserved.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reserved.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "PartsReserved",
            Version: PartsReserved.Version,
            PartitionKey: reserved.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(reserved, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static PartsReserved Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "PartsReserved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a PartsReserved envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<PartsReserved>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid PartsReserved payload: {envelope.EventId}");
    }
}
