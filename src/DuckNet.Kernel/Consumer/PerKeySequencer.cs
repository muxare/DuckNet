using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Restores per-<c>PartitionKey</c> sequence. Ordering is per key, never global.
/// Late seq (already emitted) is dropped; future seq is buffered until the gap fills.
/// A gap timeout logs only — this type does not invent or skip missing events.
/// </summary>
public sealed class PerKeySequencer
{
    private readonly Dictionary<string, KeyState> _byKey = new();

    public PerKeySequencer(IReadOnlyDictionary<string, long>? lastHandledSequenceByKey = null)
    {
        if (lastHandledSequenceByKey is null)
        {
            return;
        }

        foreach (var (key, seq) in lastHandledSequenceByKey)
        {
            if (seq < 0)
            {
                continue;
            }

            _byKey[key] = new KeyState { NextExpected = seq + 1 };
        }
    }

    public long LateDropCount { get; private set; }

    public long BufferedOverwriteCount { get; private set; }

    public long GapReportCount { get; private set; }

    public int BufferedCount => _byKey.Values.Sum(s => s.Buffer.Count);

    public IReadOnlyList<EventEnvelope> Offer(
        EventEnvelope envelope,
        DateTimeOffset? now = null)
    {
        var state = GetOrAdd(envelope.PartitionKey);
        var seq = envelope.SequenceNumber;
        var at = now ?? DateTimeOffset.UtcNow;

        if (seq < state.NextExpected)
        {
            LateDropCount++;
            return [];
        }

        if (seq > state.NextExpected)
        {
            if (!state.Buffer.TryAdd(seq, envelope))
            {
                state.Buffer[seq] = envelope;
                BufferedOverwriteCount++;
            }

            state.WaitingSince ??= at;
            return [];
        }

        var released = new List<EventEnvelope> { envelope };
        state.NextExpected++;

        while (state.Buffer.Remove(state.NextExpected, out var next))
        {
            released.Add(next);
            state.NextExpected++;
        }

        if (state.Buffer.Count == 0)
        {
            state.WaitingSince = null;
            state.GapLogged = false;
        }

        return released;
    }

    public void ReportGaps(TimeSpan timeout, TextWriter output, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;

        foreach (var (key, state) in _byKey)
        {
            if (state.WaitingSince is not { } since || state.GapLogged || at - since < timeout)
            {
                continue;
            }

            var buffered = string.Join(",", state.Buffer.Keys.OrderBy(s => s));
            output.WriteLine(
                $"Gap on {key}: waiting for seq {state.NextExpected}, buffered [{buffered}]");
            state.GapLogged = true;
            GapReportCount++;
        }
    }

    private KeyState GetOrAdd(string partitionKey)
    {
        if (!_byKey.TryGetValue(partitionKey, out var state))
        {
            state = new KeyState();
            _byKey[partitionKey] = state;
        }

        return state;
    }

    private sealed class KeyState
    {
        public long NextExpected { get; set; } = 1;

        public Dictionary<long, EventEnvelope> Buffer { get; } = new();

        public DateTimeOffset? WaitingSince { get; set; }

        public bool GapLogged { get; set; }
    }
}
