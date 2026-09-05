namespace DuckNet.Contracts;

/// <summary>
/// Frozen health-model output v1 — no ModelVersion. Upcast default is health-v1.
/// </summary>
public sealed record AssetHealthPredictedV1(
    string AssetId,
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService,
    double VibrationMmS,
    double TemperatureC,
    double EngineHours)
{
    public const int Version = 1;
}
