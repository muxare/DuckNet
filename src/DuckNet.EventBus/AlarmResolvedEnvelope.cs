using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class AlarmResolvedEnvelope
{
    public static EventEnvelope Create(
        AlarmResolved resolved,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolved.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "AlarmResolved",
            Version: AlarmResolved.Version,
            PartitionKey: resolved.DuckId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(resolved, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static AlarmResolved Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "AlarmResolved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not an AlarmResolved envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<AlarmResolved>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid AlarmResolved payload: {envelope.EventId}");
    }
}
