namespace DuckNet.Contracts;

public sealed record PartCatalogPublished(
    string Sku,
    string Name,
    int UnitCents)
{
    public const int Version = 1;
}
