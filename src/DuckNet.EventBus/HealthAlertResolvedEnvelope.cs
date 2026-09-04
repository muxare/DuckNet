using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class HealthAlertResolvedEnvelope
{
    public static EventEnvelope Create(
        HealthAlertResolved resolved,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolved.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "HealthAlertResolved",
            Version: HealthAlertResolved.Version,
            PartitionKey: resolved.AssetId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(resolved, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static HealthAlertResolved Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "HealthAlertResolved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a HealthAlertResolved envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<HealthAlertResolved>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid HealthAlertResolved payload: {envelope.EventId}");
    }
}
