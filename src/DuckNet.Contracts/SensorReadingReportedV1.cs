namespace DuckNet.Contracts;

/// <summary>
/// Frozen OrePart ingest v1 — no tenant field. Mixed logs upcast at the consumer.
/// </summary>
public sealed record SensorReadingReportedV1(
    string AssetId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    double EngineHours,
    double VibrationMmS,
    double TemperatureC)
{
    public const int Version = 1;
}
