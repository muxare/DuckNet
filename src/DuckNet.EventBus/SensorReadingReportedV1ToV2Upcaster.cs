using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// v1 has no tenant. Default is tenant-default (unknown), not an inferred site.
/// EventId, partition key, and sequence stay the same — this is not a new fact.
/// </summary>
public sealed class SensorReadingReportedV1ToV2Upcaster : IEventUpcaster
{
    public bool CanUpcast(string type, int version) =>
        string.Equals(type, "SensorReadingReported", StringComparison.Ordinal)
        && version == SensorReadingReportedV1.Version;

    public EventEnvelope Upcast(EventEnvelope source)
    {
        if (!CanUpcast(source.Type, source.Version))
        {
            throw new InvalidOperationException(
                $"Cannot upcast {source.Type} v{source.Version} (EventId={source.EventId}).");
        }

        var v1 = JsonSerializer.Deserialize<SensorReadingReportedV1>(source.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid SensorReadingReported v1 payload: {source.EventId}");

        var v2 = new SensorReadingReported(
            v1.AssetId,
            v1.SequenceNumber,
            v1.OccurredAt,
            v1.EngineHours,
            v1.VibrationMmS,
            v1.TemperatureC,
            OrePartTenants.Default);
        return source with
        {
            Version = SensorReadingReported.Version,
            PayloadJson = JsonSerializer.Serialize(v2, EnvelopeJson.Options)
        };
    }
}
