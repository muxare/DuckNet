using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class FeeReleasedEnvelope
{
    public static EventEnvelope Create(
        FeeReleased released,
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
            Type: "FeeReleased",
            Version: FeeReleased.Version,
            PartitionKey: released.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(released, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static FeeReleased Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "FeeReleased", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a FeeReleased envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<FeeReleased>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid FeeReleased payload: {envelope.EventId}");
    }
}
