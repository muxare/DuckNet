using System.Text.Json;
using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.Kernel;

/// <summary>
/// Test/demo poison: a well-formed envelope whose payload cannot parse.
/// </summary>
public static class PoisonEvents
{
    public const string PayloadJson = "{not-json";

    public const string DefaultPartitionKey = "poison-duck";

    public static EventEnvelope MalformedSqueaked(
        string partitionKey = DefaultPartitionKey,
        long sequenceNumber = 1,
        Guid? eventId = null) =>
        new(
            EventId: eventId ?? Guid.NewGuid(),
            Type: "Squeaked",
            Version: Squeaked.Version,
            PartitionKey: partitionKey,
            SequenceNumber: sequenceNumber,
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: PayloadJson);

    public static EventEnvelope WithValidSqueakedPayload(EventEnvelope envelope)
    {
        var squeaked = new Squeaked(
            envelope.PartitionKey,
            envelope.SequenceNumber,
            envelope.OccurredAt,
            VolumeDb: 60);
        return envelope with
        {
            Version = Squeaked.Version,
            PayloadJson = JsonSerializer.Serialize(squeaked, EnvelopeJson.Options)
        };
    }
}
