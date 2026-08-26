using System.Text.Json;
using DuckNet.Kernel.Domain.Events;

namespace DuckNet.Kernel.Transport;

public static class SqueakedEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static EventEnvelope Create(Squeaked squeaked, Guid? eventId = null) =>
        new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "Squeaked",
            Version: 1,
            PartitionKey: squeaked.DuckId,
            SequenceNumber: squeaked.SequenceNumber,
            OccurredAt: squeaked.OccurredAt,
            PayloadJson: JsonSerializer.Serialize(squeaked, JsonOptions));

    public static Squeaked Parse(EventEnvelope envelope) =>
        JsonSerializer.Deserialize<Squeaked>(envelope.PayloadJson, JsonOptions)
        ?? throw new InvalidOperationException($"Invalid Squeaked payload: {envelope.EventId}");
}
