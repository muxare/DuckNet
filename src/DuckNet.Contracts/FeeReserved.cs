namespace DuckNet.Contracts;

public sealed record FeeReserved(
    Guid AlarmId,
    string DuckId,
    int AmountCents,
    DateTimeOffset ExpiresAt)
{
    public const int Version = 1;
}
