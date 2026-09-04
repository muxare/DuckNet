namespace DuckNet.Contracts;

public sealed record PartsReserved(
    Guid AlarmId,
    string AssetId,
    IReadOnlyList<PartLine> Lines,
    int TotalCents,
    DateTimeOffset ExpiresAt)
{
    public const int Version = 1;
}
