using DuckNet.Contracts;

namespace DuckNet.AlarmCenter;

/// <summary>
/// Transparent OrePart health model. Weights are documented, not learned:
/// 45% vibration, 35% temperature, 20% engine hours, plus 0.10 if vibration
/// is rising versus the previous reading.
/// </summary>
public static class HealthScorer
{
    public const double RaiseThreshold = 0.70;
    public const double ResolveThreshold = 0.50;

    public const double HealthyVibrationMmS = 2.0;
    public const double FailedVibrationMmS = 12.0;
    public const double HealthyTemperatureC = 70.0;
    public const double FailedTemperatureC = 110.0;
    public const double HoursBaseline = 8000.0;
    public const double HoursSpan = 8000.0;
    public const double HorizonHoursAtZeroScore = 500.0;
    public const double TrendBonus = 0.10;

    public const double V2VibrationWeight = 0.60;
    public const double V2TemperatureWeight = 0.25;
    public const double V2HoursWeight = 0.15;

    public static HealthScore Score(SensorReadingReported reading, double? previousVibrationMmS) =>
        Score(reading, previousVibrationMmS, HealthModels.V1);

    public static HealthScore Score(
        SensorReadingReported reading,
        double? previousVibrationMmS,
        string modelVersion)
    {
        var vibrationNorm = Clamp01(
            (reading.VibrationMmS - HealthyVibrationMmS) / (FailedVibrationMmS - HealthyVibrationMmS));
        var tempNorm = Clamp01(
            (reading.TemperatureC - HealthyTemperatureC) / (FailedTemperatureC - HealthyTemperatureC));
        var hoursNorm = Clamp01((reading.EngineHours - HoursBaseline) / HoursSpan);
        var trend = previousVibrationMmS is { } prev && reading.VibrationMmS > prev + 0.5
            ? TrendBonus
            : 0;

        var (vw, tw, hw) = string.Equals(modelVersion, HealthModels.V2, StringComparison.Ordinal)
            ? (V2VibrationWeight, V2TemperatureWeight, V2HoursWeight)
            : (0.45, 0.35, 0.20);

        var score = Clamp01((vw * vibrationNorm) + (tw * tempNorm) + (hw * hoursNorm) + trend);
        var hoursRemaining = (1.0 - score) * HorizonHoursAtZeroScore;
        var predictedFailureAt = reading.OccurredAt.AddHours(hoursRemaining);
        var recommended = Recommend(vibrationNorm, tempNorm, hoursNorm);
        return new HealthScore(
            score,
            predictedFailureAt,
            recommended,
            vibrationNorm,
            tempNorm,
            hoursNorm,
            trend,
            string.IsNullOrWhiteSpace(modelVersion) ? HealthModels.V1 : modelVersion);
    }

    public static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static string Recommend(double vibrationNorm, double tempNorm, double hoursNorm)
    {
        if (vibrationNorm >= tempNorm && vibrationNorm >= hoursNorm)
        {
            return "Replace drive-axle bearing kit";
        }

        if (tempNorm >= hoursNorm)
        {
            return "Inspect hydraulic cooling circuit";
        }

        return "Schedule engine overhaul";
    }
}

public sealed record HealthScore(
    double Score,
    DateTimeOffset PredictedFailureAt,
    string RecommendedService,
    double VibrationNorm,
    double TemperatureNorm,
    double HoursNorm,
    double TrendBonus,
    string ModelVersion = HealthModels.V1);
