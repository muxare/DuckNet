namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Consumer-owned idempotency set. Dedup key is <c>EventId</c>, not payload.
/// In-memory for Step 1–2; Step 3 persists per consumer group.
/// Sequencer may drop late seq before this set is consulted.
/// </summary>
public sealed class Inbox
{
    private readonly HashSet<Guid> _processed = [];
    private readonly bool _enabled;

    public Inbox(string consumerGroup, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ConsumerGroup = consumerGroup;
        _enabled = enabled;
    }

    public string ConsumerGroup { get; }

    public long DuplicateSkipCount { get; private set; }

    public bool ShouldHandle(Guid eventId)
    {
        if (!_enabled)
        {
            return true;
        }

        if (_processed.Contains(eventId))
        {
            DuplicateSkipCount++;
            return false;
        }

        return true;
    }

    public void MarkProcessed(Guid eventId)
    {
        if (!_enabled)
        {
            return;
        }

        _processed.Add(eventId);
    }
}
