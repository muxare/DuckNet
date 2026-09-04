namespace DuckNet.Contracts;

public sealed record PartLine(
    string Sku,
    int Quantity,
    int UnitCents);
