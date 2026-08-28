using System.Globalization;

namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Stable key-hash assignment. Same <c>PartitionKey</c> always lands on the same
/// shard so per-key order is preserved. Not part of the bus.
/// </summary>
public static class PartitionShard
{
    public const int DefaultCount = 3;

    public const int DefaultCapacity = 32;

    public static int Assign(string partitionKey, int shardCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        if (shardCount == 1)
        {
            return 0;
        }

        return (int)(Fnv1a32(partitionKey) % (uint)shardCount);
    }

    public static string FirstKeyOnShard(int shard, int shardCount, string prefix = "duck-")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shard, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shard, shardCount);

        for (var i = 1; i < 10_000; i++)
        {
            var key = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (Assign(key, shardCount) == shard)
            {
                return key;
            }
        }

        throw new InvalidOperationException($"No key on shard {shard} of {shardCount}.");
    }

    private static uint Fnv1a32(string value)
    {
        uint hash = 2166136261;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }
}
