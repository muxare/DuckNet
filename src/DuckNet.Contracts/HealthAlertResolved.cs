namespace DuckNet.Contracts;

public sealed record HealthAlertResolved(
    string AssetId,
    DateTimeOffset ResolvedAt)
{
    public const int Version = 1;
}
