using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class BasketRevisedEnvelope
{
    public static EventEnvelope Create(
        BasketRevised revised,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(revised.AlarmId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(revised.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "BasketRevised",
            Version: BasketRevised.Version,
            PartitionKey: revised.AlarmId.ToString(),
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(revised, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static BasketRevised Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "BasketRevised", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a BasketRevised envelope: {envelope.Type} ({envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<BasketRevised>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid BasketRevised payload: {envelope.EventId}");
    }
}
