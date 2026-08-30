namespace DuckNet.Contracts;

public sealed record AlarmResolved(
    string DuckId,
    DateTimeOffset ResolvedAt)
{
    public const int Version = 1;
}
