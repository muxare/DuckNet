using System.Collections.Concurrent;
using DuckNet.Contracts;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Per-shard queue lag and per-key processing lag. Consumer-owned; not a bus metric.
/// </summary>
public sealed class ShardMetrics
{
    private readonly ShardCounters[] _shards;
    private readonly ConcurrentDictionary<string, KeyCounters> _keys = new(StringComparer.Ordinal);

    public ShardMetrics(int shardCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        _shards = new ShardCounters[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            _shards[i] = new ShardCounters();
        }
    }

    public int ShardCount => _shards.Length;

    public void RecordEnqueue(int shard, EventEnvelope envelope)
    {
        var counters = _shards[shard];
        Interlocked.Increment(ref counters.Enqueued);
        InterlockedMax(ref counters.MaxOffset, envelope.LogOffset);
    }

    public void RecordDequeue(int shard) =>
        Interlocked.Increment(ref _shards[shard].Dequeued);

    public void RecordBackpressure(int shard) =>
        Interlocked.Increment(ref _shards[shard].Backpressure);

    public void RecordProcessed(int shard, EventEnvelope envelope, TimeSpan lag)
    {
        var counters = _shards[shard];
        Interlocked.Increment(ref counters.Processed);
        InterlockedMax(ref counters.LastOffset, envelope.LogOffset);

        var lagMs = (long)Math.Max(0, lag.TotalMilliseconds);
        var key = _keys.GetOrAdd(envelope.PartitionKey, _ => new KeyCounters { Shard = shard });
        Interlocked.Increment(ref key.Processed);
        Interlocked.Exchange(ref key.LastLagMs, lagMs);
        InterlockedMax(ref key.MaxLagMs, lagMs);
    }

    public void Reset()
    {
        foreach (var shard in _shards)
        {
            Interlocked.Exchange(ref shard.Enqueued, 0);
            Interlocked.Exchange(ref shard.Dequeued, 0);
            Interlocked.Exchange(ref shard.Processed, 0);
            Interlocked.Exchange(ref shard.Backpressure, 0);
            Interlocked.Exchange(ref shard.MaxOffset, 0);
            Interlocked.Exchange(ref shard.LastOffset, 0);
        }

        _keys.Clear();
    }

    public ShardMetricsSnapshot Snapshot(IReadOnlyList<int> queuedByShard)
    {
        ArgumentNullException.ThrowIfNull(queuedByShard);
        var shards = new ShardSnapshot[_shards.Length];
        for (var i = 0; i < _shards.Length; i++)
        {
            var c = _shards[i];
            var queued = i < queuedByShard.Count ? queuedByShard[i] : 0;
            var max = Interlocked.Read(ref c.MaxOffset);
            var last = Interlocked.Read(ref c.LastOffset);
            shards[i] = new ShardSnapshot(
                Id: i,
                Queued: queued,
                Lag: Math.Max(0, max - last),
                LastOffset: last,
                MaxOffset: max,
                Backpressure: Interlocked.Read(ref c.Backpressure),
                Processed: Interlocked.Read(ref c.Processed));
        }

        var keys = _keys.Select(pair => new KeyLagSnapshot(
                pair.Key,
                pair.Value.Shard,
                Interlocked.Read(ref pair.Value.LastLagMs),
                Interlocked.Read(ref pair.Value.MaxLagMs),
                Interlocked.Read(ref pair.Value.Processed)))
            .OrderBy(k => k.PartitionKey, StringComparer.Ordinal)
            .ToArray();

        return new ShardMetricsSnapshot(shards, keys);
    }

    private static void InterlockedMax(ref long location, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref location);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class ShardCounters
    {
        public long Enqueued;
        public long Dequeued;
        public long Processed;
        public long Backpressure;
        public long MaxOffset;
        public long LastOffset;
    }

    private sealed class KeyCounters
    {
        public int Shard;
        public long Processed;
        public long LastLagMs;
        public long MaxLagMs;
    }
}

public sealed record ShardMetricsSnapshot(
    IReadOnlyList<ShardSnapshot> Shards,
    IReadOnlyList<KeyLagSnapshot> Keys);

public sealed record ShardSnapshot(
    int Id,
    int Queued,
    long Lag,
    long LastOffset,
    long MaxOffset,
    long Backpressure,
    long Processed);

public sealed record KeyLagSnapshot(
    string PartitionKey,
    int Shard,
    long LastLagMs,
    long MaxLagMs,
    long Processed);
