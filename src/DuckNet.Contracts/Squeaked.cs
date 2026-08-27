namespace DuckNet.Contracts;

public sealed record Squeaked(
    string DuckId,
    long SequenceNumber,
    DateTimeOffset OccurredAt);
