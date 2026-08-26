namespace DuckNet.Kernel.Domain.Events;

public sealed record Squeaked(
    string DuckId,
    long SequenceNumber,
    DateTimeOffset OccurredAt);
