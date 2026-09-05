namespace DuckNet.Contracts;

public sealed record BasketRevised(
    Guid AlarmId,
    string AssetId,
    IReadOnlyList<PartLine> Lines,
    int TotalCents)
{
    public const int Version = 1;
}
