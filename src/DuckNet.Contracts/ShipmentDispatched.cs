namespace DuckNet.Contracts;

public sealed record ShipmentDispatched(
    Guid OrderId,
    Guid AlarmId,
    string AssetId,
    DateTimeOffset ShippedAt)
{
    public const int Version = 1;
}
