using DuckNet.Contracts;

namespace DuckNet.AlarmCenter.Tests;

public class HealthScorerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    [Fact]
    public void Healthy_reading_scores_near_zero()
    {
        var reading = new SensorReadingReported("TRK-002", 1, At, 4000, 1.8, 70);
        var score = HealthScorer.Score(reading, previousVibrationMmS: null);
        Assert.True(score.Score < 0.2, $"score was {score.Score}");
        Assert.Equal("Replace drive-axle bearing kit", score.RecommendedService);
    }

    [Fact]
    public void High_vibration_with_trend_crosses_raise_threshold()
    {
        var reading = new SensorReadingReported("TRK-001", 20, At, 9200, 12, 110);
        var score = HealthScorer.Score(reading, previousVibrationMmS: 8);
        Assert.True(score.Score >= HealthScorer.RaiseThreshold, $"score was {score.Score}");
        Assert.Equal(HealthScorer.TrendBonus, score.TrendBonus);
        Assert.True(score.PredictedFailureAt <= At.AddHours(150));
    }

    [Fact]
    public void Score_is_deterministic()
    {
        var reading = new SensorReadingReported("TRK-001", 5, At, 9000, 8, 90);
        var a = HealthScorer.Score(reading, 7);
        var b = HealthScorer.Score(reading, 7);
        Assert.Equal(a, b);
    }
}
