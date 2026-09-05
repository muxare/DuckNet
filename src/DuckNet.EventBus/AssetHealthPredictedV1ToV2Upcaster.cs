using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// v1 has no ModelVersion. Default is health-v1 (the documented original formula).
/// </summary>
public sealed class AssetHealthPredictedV1ToV2Upcaster : IEventUpcaster
{
    public bool CanUpcast(string type, int version) =>
        string.Equals(type, "AssetHealthPredicted", StringComparison.Ordinal)
        && version == AssetHealthPredictedV1.Version;

    public EventEnvelope Upcast(EventEnvelope source)
    {
        if (!CanUpcast(source.Type, source.Version))
        {
            throw new InvalidOperationException(
                $"Cannot upcast {source.Type} v{source.Version} (EventId={source.EventId}).");
        }

        var v1 = JsonSerializer.Deserialize<AssetHealthPredictedV1>(source.PayloadJson, EnvelopeJson.Options)
            ?? throw new InvalidOperationException($"Invalid AssetHealthPredicted v1 payload: {source.EventId}");

        var v2 = new AssetHealthPredicted(
            v1.AssetId,
            v1.Score,
            v1.PredictedFailureAt,
            v1.RecommendedService,
            v1.VibrationMmS,
            v1.TemperatureC,
            v1.EngineHours,
            HealthModels.V1);
        return source with
        {
            Version = AssetHealthPredicted.Version,
            PayloadJson = JsonSerializer.Serialize(v2, EnvelopeJson.Options)
        };
    }
}
