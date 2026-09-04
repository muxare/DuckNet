using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class PartsReleasedEnvelope
{
    public static EventEnvelope Create(
        PartsReleased released,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(released.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(released.Reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "PartsReleased",
            Version: PartsReleased.Version,
            PartitionKey: released.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(released, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static PartsReleased Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "PartsReleased", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a PartsReleased envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<PartsReleased>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid PartsReleased payload: {envelope.EventId}");
    }
}
