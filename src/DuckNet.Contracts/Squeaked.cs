namespace DuckNet.Contracts;

/// <summary>
/// Frozen v1 wire shape. New events are <see cref="Squeaked"/> (v2).
/// Consumers upcast v1 → v2 before the handler runs.
/// </summary>
public sealed record SqueakedV1(
    string DuckId,
    long SequenceNumber,
    DateTimeOffset OccurredAt)
{
    public const int Version = 1;
}

/// <summary>
/// Current Squeaked contract (v2). Adds <see cref="VolumeDb"/>.
/// Handlers parse this type only, after <c>EventUpcasterPipeline.Upcast</c>.
/// </summary>
public sealed record Squeaked(
    string DuckId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    double VolumeDb = 0)
{
    public const int Version = 2;
}
