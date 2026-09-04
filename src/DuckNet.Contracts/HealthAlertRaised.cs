namespace DuckNet.Contracts;

public sealed record HealthAlertRaised(
    string AssetId,
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService)
{
    public const int Version = 1;
}
