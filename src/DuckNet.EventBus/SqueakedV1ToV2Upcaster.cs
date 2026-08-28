using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// v1 has no volume. Default is 0 (unknown), not an estimate.
/// EventId, partition key, and sequence stay the same — this is not a new fact.
/// </summary>
public sealed class SqueakedV1ToV2Upcaster : IEventUpcaster
{
    public const double DefaultVolumeDb = 0;

    public bool CanUpcast(string type, int version) =>
        string.Equals(type, "Squeaked", StringComparison.Ordinal) && version == SqueakedV1.Version;

    public EventEnvelope Upcast(EventEnvelope source)
    {
        if (!CanUpcast(source.Type, source.Version))
        {
            throw new InvalidOperationException(
                $"Cannot upcast {source.Type} v{source.Version} (EventId={source.EventId}).");
        }

        var v1 = JsonSerializer.Deserialize<SqueakedV1>(source.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid Squeaked v1 payload: {source.EventId}");

        var v2 = new Squeaked(v1.DuckId, v1.SequenceNumber, v1.OccurredAt, DefaultVolumeDb);
        return source with
        {
            Version = Squeaked.Version,
            PayloadJson = JsonSerializer.Serialize(v2, EnvelopeJson.Options)
        };
    }
}
