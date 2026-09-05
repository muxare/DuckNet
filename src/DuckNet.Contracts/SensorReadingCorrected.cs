namespace DuckNet.Contracts;

/// <summary>
/// Replacement reading for an earlier fact. New EventId; does not mutate the log.
/// </summary>
public sealed record SensorReadingCorrected(
    string AssetId,
    long SequenceNumber,
    Guid SupersedesEventId,
    DateTimeOffset OccurredAt,
    double EngineHours,
    double VibrationMmS,
    double TemperatureC,
    string TenantId = OrePartTenants.Default)
{
    public const int Version = 1;
}
