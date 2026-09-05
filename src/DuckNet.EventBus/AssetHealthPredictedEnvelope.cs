using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class AssetHealthPredictedEnvelope
{
    public static EventEnvelope Create(
        AssetHealthPredicted predicted,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicted.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "AssetHealthPredicted",
            Version: AssetHealthPredicted.Version,
            PartitionKey: predicted.AssetId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(predicted, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static EventEnvelope CreateV1(
        AssetHealthPredictedV1 predicted,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null,
        string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicted.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "AssetHealthPredicted",
            Version: AssetHealthPredictedV1.Version,
            PartitionKey: predicted.AssetId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(predicted, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static AssetHealthPredicted Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "AssetHealthPredicted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not an AssetHealthPredicted envelope: {envelope.Type} ({envelope.EventId})");
        }

        if (envelope.Version != AssetHealthPredicted.Version)
        {
            throw new InvalidOperationException(
                $"AssetHealthPredicted v{envelope.Version} must be upcast to v{AssetHealthPredicted.Version} before parse (EventId={envelope.EventId})");
        }

        return System.Text.Json.JsonSerializer.Deserialize<AssetHealthPredicted>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid AssetHealthPredicted payload: {envelope.EventId}");
    }
}
