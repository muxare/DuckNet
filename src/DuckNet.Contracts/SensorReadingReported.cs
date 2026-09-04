namespace DuckNet.Contracts;

/// <summary>
/// OrePart ingest fact (v1). Asset telemetry: hours, vibration, temperature.
/// Frozen wire shape — new fields require v2 + an upcaster.
/// </summary>
public sealed record SensorReadingReported(
    string AssetId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    double EngineHours,
    double VibrationMmS,
    double TemperatureC)
{
    public const int Version = 1;
}
