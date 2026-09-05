using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class SensorReadingCorrectedEnvelope
{
    public static EventEnvelope Create(
        SensorReadingCorrected corrected,
        Guid? eventId = null,
        string? traceId = null,
        string? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corrected.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(corrected.SequenceNumber, 1);
        ArgumentOutOfRangeException.ThrowIfEqual(corrected.SupersedesEventId, Guid.Empty);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "SensorReadingCorrected",
            Version: SensorReadingCorrected.Version,
            PartitionKey: corrected.AssetId,
            SequenceNumber: corrected.SequenceNumber,
            OccurredAt: corrected.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(corrected, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId ?? corrected.SupersedesEventId.ToString());
    }

    public static SensorReadingCorrected Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "SensorReadingCorrected", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a SensorReadingCorrected envelope: {envelope.Type} ({envelope.EventId})");
        }

        return JsonSerializer.Deserialize<SensorReadingCorrected>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid SensorReadingCorrected payload: {envelope.EventId}");
    }
}
