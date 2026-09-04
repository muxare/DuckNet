namespace DuckNet.Contracts;

public sealed record OrderConfirmed(
    Guid OrderId,
    Guid AlarmId,
    string AssetId,
    IReadOnlyList<PartLine> Lines,
    int TotalCents,
    DateTimeOffset ConfirmedAt)
{
    public const int Version = 1;
}
