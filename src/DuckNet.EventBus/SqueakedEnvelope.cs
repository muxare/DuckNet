using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class SqueakedEnvelope
{
    /// <summary>
    /// Wire format for current <see cref="Squeaked"/> (v2). PartitionKey is the duck id;
    /// SequenceNumber is monotonic per key, never a global clock.
    /// </summary>
    public static EventEnvelope Create(Squeaked squeaked, Guid? eventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(squeaked.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(squeaked.SequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "Squeaked",
            Version: Squeaked.Version,
            PartitionKey: squeaked.DuckId,
            SequenceNumber: squeaked.SequenceNumber,
            OccurredAt: squeaked.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(squeaked, EnvelopeJson.Options));
    }

    /// <summary>
    /// Frozen v1 wire format for mixed-log replay tests. New producers must not call this.
    /// </summary>
    public static EventEnvelope CreateV1(SqueakedV1 squeaked, Guid? eventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(squeaked.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(squeaked.SequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "Squeaked",
            Version: SqueakedV1.Version,
            PartitionKey: squeaked.DuckId,
            SequenceNumber: squeaked.SequenceNumber,
            OccurredAt: squeaked.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(squeaked, EnvelopeJson.Options));
    }

    public static Squeaked Parse(EventEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, "Squeaked", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a Squeaked envelope: {envelope.Type} ({envelope.EventId})");
        }

        if (envelope.Version != Squeaked.Version)
        {
            throw new InvalidOperationException(
                $"Squeaked v{envelope.Version} must be upcast to v{Squeaked.Version} before parse (EventId={envelope.EventId})");
        }

        return JsonSerializer.Deserialize<Squeaked>(envelope.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid Squeaked payload: {envelope.EventId}");
    }
}
