namespace DuckNet.Contracts;

/// <summary>
/// Deterministic health-model output. Not a command and not an ML score —
/// a documented formula over the triggering reading, tagged with a model version.
/// </summary>
public sealed record AssetHealthPredicted(
    string AssetId,
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService,
    double VibrationMmS,
    double TemperatureC,
    double EngineHours,
    string ModelVersion = HealthModels.V1)
{
    public const int Version = 2;
}
