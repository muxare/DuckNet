using System.Threading.Channels;
using DuckNet.Contracts;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// One channel per shard. Capacity is a backpressure <i>signal</i> (count when
/// queued ≥ capacity) — the dispatcher never blocks on a hot shard, or quiet
/// keys would starve at the enqueue loop. Workers are single-threaded so a key
/// hashed to one shard keeps per-key order. Not part of the bus.
/// </summary>
public sealed class ShardWorkerPool : IAsyncDisposable
{
    private readonly Channel<EventEnvelope>[] _channels;
    private readonly Task[] _workers;
    private readonly Func<EventEnvelope, CancellationToken, Task> _handle;
    private readonly int _capacity;
    private readonly long[] _queued;
    private long _inflight;

    public ShardWorkerPool(
        int shardCount,
        Func<EventEnvelope, CancellationToken, Task> handle,
        int capacity = PartitionShard.DefaultCapacity,
        ShardMetrics? metrics = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(handle);

        _handle = handle;
        _capacity = capacity;
        Metrics = metrics ?? new ShardMetrics(shardCount);
        if (Metrics.ShardCount != shardCount)
        {
            throw new ArgumentException("Metrics shard count must match the pool.", nameof(metrics));
        }

        _channels = new Channel<EventEnvelope>[shardCount];
        _queued = new long[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            _channels[i] = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        _workers = new Task[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            var shard = i;
            _workers[i] = Task.Run(() => RunWorkerAsync(shard));
        }
    }

    public ShardMetrics Metrics { get; }

    public int ShardCount => _channels.Length;

    public async ValueTask DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var shard = PartitionShard.Assign(envelope.PartitionKey, _channels.Length);
        var queued = Interlocked.Read(ref _queued[shard]);
        if (queued >= _capacity)
        {
            Metrics.RecordBackpressure(shard);
        }

        Metrics.RecordEnqueue(shard, envelope);
        Interlocked.Increment(ref _queued[shard]);
        try
        {
            await _channels[shard].Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _queued[shard]);
            throw;
        }
    }

    public ShardMetricsSnapshot Snapshot()
    {
        var queued = new int[_channels.Length];
        for (var i = 0; i < _channels.Length; i++)
        {
            queued[i] = (int)Math.Max(0, Interlocked.Read(ref _queued[i]));
        }

        return Metrics.Snapshot(queued);
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _inflight) > 0 || HasQueued())
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasQueued()
    {
        for (var i = 0; i < _queued.Length; i++)
        {
            if (Interlocked.Read(ref _queued[i]) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunWorkerAsync(int shard)
    {
        await foreach (var envelope in _channels[shard].Reader.ReadAllAsync())
        {
            Interlocked.Increment(ref _inflight);
            try
            {
                Metrics.RecordDequeue(shard);
                Interlocked.Decrement(ref _queued[shard]);
                await _handle(envelope, CancellationToken.None).ConfigureAwait(false);
                Metrics.RecordProcessed(shard, envelope, DateTimeOffset.UtcNow - envelope.OccurredAt);
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        }
    }
}
