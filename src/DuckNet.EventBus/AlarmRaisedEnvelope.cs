using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

public static class AlarmRaisedEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static EventEnvelope Create(
        AlarmRaised raised,
        long sequenceNumber,
        Guid? eventId = null,
        string? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raised.DuckId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        return new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "AlarmRaised",
            Version: 1,
            PartitionKey: raised.DuckId,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: JsonSerializer.Serialize(raised, JsonOptions),
            CausationId: causationId);
    }

    public static AlarmRaised Parse(EventEnvelope envelope) =>
        JsonSerializer.Deserialize<AlarmRaised>(envelope.PayloadJson, JsonOptions)
        ?? throw new InvalidOperationException($"Invalid AlarmRaised payload: {envelope.EventId}");
}
