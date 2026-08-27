using System.Text.Json;
using DuckNet.Kernel.Domain.Events;

namespace DuckNet.Kernel.Transport;

public static class SqueakedEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Wire format for <see cref="Squeaked"/>. PartitionKey is the duck id;
    /// SequenceNumber is monotonic per key, never a global clock.
    /// </summary>
    public static EventEnvelope Create(Squeaked squeaked, Guid? eventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(squeaked.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(squeaked.SequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "Squeaked",
            Version: 1,
            PartitionKey: squeaked.DuckId,
            SequenceNumber: squeaked.SequenceNumber,
            OccurredAt: squeaked.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(squeaked, JsonOptions));
    }

    public static Squeaked Parse(EventEnvelope envelope) =>
        JsonSerializer.Deserialize<Squeaked>(envelope.PayloadJson, JsonOptions)
        ?? throw new InvalidOperationException($"Invalid Squeaked payload: {envelope.EventId}");
}
