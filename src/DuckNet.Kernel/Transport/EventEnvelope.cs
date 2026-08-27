namespace DuckNet.Kernel.Transport;

public sealed record EventEnvelope(
    Guid EventId,
    string Type,
    int Version,
    string PartitionKey,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    string PayloadJson,
    string? TraceId = null,
    string? CausationId = null,
    // Assigned when the row is appended to event_log; 0 until then.
    long LogOffset = 0);
