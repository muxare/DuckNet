using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class SensorReadingReportedEnvelope
{
    public static EventEnvelope Create(
        SensorReadingReported reading,
        Guid? eventId = null,
        string? traceId = null,
        string? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(reading.SequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "SensorReadingReported",
            Version: SensorReadingReported.Version,
            PartitionKey: reading.AssetId,
            SequenceNumber: reading.SequenceNumber,
            OccurredAt: reading.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(reading, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static EventEnvelope CreateV1(
        SensorReadingReportedV1 reading,
        Guid? eventId = null,
        string? traceId = null,
        string? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.AssetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(reading.SequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "SensorReadingReported",
            Version: SensorReadingReportedV1.Version,
            PartitionKey: reading.AssetId,
            SequenceNumber: reading.SequenceNumber,
            OccurredAt: reading.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(reading, EnvelopeJson.Options),
            TraceId: traceId,
            CausationId: causationId);
    }

    public static SensorReadingReported Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "SensorReadingReported", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a SensorReadingReported envelope: {envelope.Type} ({envelope.EventId})");
        }

        if (envelope.Version != SensorReadingReported.Version)
        {
            throw new InvalidOperationException(
                $"SensorReadingReported v{envelope.Version} must be upcast to v{SensorReadingReported.Version} before parse (EventId={envelope.EventId})");
        }

        return JsonSerializer.Deserialize<SensorReadingReported>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid SensorReadingReported payload: {envelope.EventId}");
    }
}
