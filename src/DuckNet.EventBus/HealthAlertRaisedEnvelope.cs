using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class HealthAlertRaisedEnvelope
{
    public static EventEnvelope Create(
        HealthAlertRaised raised,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raised.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "HealthAlertRaised",
            Version: HealthAlertRaised.Version,
            PartitionKey: raised.AssetId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(raised, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static HealthAlertRaised Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "HealthAlertRaised", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a HealthAlertRaised envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<HealthAlertRaised>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid HealthAlertRaised payload: {envelope.EventId}");
    }
}
