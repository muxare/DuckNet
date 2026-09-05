namespace DuckNet.Contracts;

public sealed record FitmentChanged(
    string EquipmentModel,
    string Sku,
    int Quantity)
{
    public const int Version = 1;
}
