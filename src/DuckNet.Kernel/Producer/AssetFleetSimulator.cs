using System.Diagnostics;
using DuckNet.EventBus;

namespace DuckNet.Kernel.Producer;

/// <summary>
/// Seeded OrePart fleet. One asset (<see cref="DegradedAssetId"/>) follows a
/// rising vibration/temperature curve so the health scorer can raise an alert.
/// </summary>
public sealed class AssetFleetSimulator : ITelemetrySimulator
{
    public const string DefaultDegradedAssetId = "TRK-001";

    public static readonly AssetProfile[] Catalog =
    [
        new("TRK-001", "CAT-797", 9200, 2.1, 74),
        new("TRK-002", "CAT-797", 4100, 1.8, 71),
        new("TRK-003", "CAT-777", 5300, 2.0, 72),
        new("DRL-001", "SANDVIK-D45", 6100, 1.6, 68),
        new("DRL-002", "SANDVIK-D45", 2800, 1.5, 67),
        new("LDR-001", "CAT-994K", 4400, 1.9, 70),
        new("LDR-002", "CAT-994K", 10200, 2.2, 76),
        new("CRU-001", "METSO-HP400", 7000, 2.4, 73)
    ];

    private readonly TransactionalPublisher _publisher;
    private readonly AssetProfile[] _fleet;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random;
    private readonly string _degradedAssetId;
    private readonly ActivitySource _activitySource;
    private readonly Dictionary<string, int> _emitCounts = new(StringComparer.Ordinal);

    public AssetFleetSimulator(
        TransactionalPublisher publisher,
        int assetCount = 8,
        int? seed = 42,
        int minDelayMs = 20,
        int maxDelayMs = 80,
        string? degradedAssetId = DefaultDegradedAssetId,
        ActivitySource? activitySource = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(assetCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minDelayMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelayMs, minDelayMs);

        _publisher = publisher;
        _fleet = Catalog.Take(Math.Min(assetCount, Catalog.Length)).ToArray();
        if (_fleet.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assetCount));
        }

        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        _degradedAssetId = string.IsNullOrWhiteSpace(degradedAssetId)
            ? DefaultDegradedAssetId
            : degradedAssetId;
        _activitySource = activitySource ?? DuckNetTracing.Telemetry;
        foreach (var asset in _fleet)
        {
            _emitCounts[asset.AssetId] = 0;
        }
    }

    public string DegradedAssetId => _degradedAssetId;

    public IReadOnlyList<AssetProfile> Fleet => _fleet;

    public long PublishedCount { get; private set; }

    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var endAt = duration == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < endAt && !cancellationToken.IsCancellationRequested)
        {
            var asset = _fleet[_random.Next(_fleet.Length)];
            await EmitAsync(asset, cancellationToken);
            await Task.Delay(_random.Next(_minDelayMs, _maxDelayMs + 1), cancellationToken);
        }
    }

    public async Task PublishOneAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var asset = _fleet.FirstOrDefault(a => string.Equals(a.AssetId, assetId, StringComparison.Ordinal))
            ?? throw new ArgumentOutOfRangeException(nameof(assetId), assetId, "Unknown asset.");
        await EmitAsync(asset, cancellationToken);
    }

    public Task PublishNextAsync(CancellationToken cancellationToken = default)
    {
        var asset = _fleet[_random.Next(_fleet.Length)];
        return EmitAsync(asset, cancellationToken);
    }

    public (double EngineHours, double VibrationMmS, double TemperatureC) Sample(string assetId, int emitIndex)
    {
        var asset = Catalog.First(a => string.Equals(a.AssetId, assetId, StringComparison.Ordinal));
        return Sample(asset, emitIndex);
    }

    private async Task EmitAsync(AssetProfile asset, CancellationToken cancellationToken)
    {
        var n = _emitCounts[asset.AssetId]++;
        var (hours, vibration, temp) = Sample(asset, n);
        using var activity = DuckNetTracing.StartProducer(_activitySource, "simulate.reading", asset.AssetId);
        await _publisher.PublishSensorReadingAsync(asset.AssetId, hours, vibration, temp, cancellationToken);
        PublishedCount++;
    }

    private (double EngineHours, double VibrationMmS, double TemperatureC) Sample(AssetProfile asset, int emitIndex)
    {
        var hours = asset.BaseHours + (emitIndex * 0.25);
        if (string.Equals(asset.AssetId, _degradedAssetId, StringComparison.Ordinal))
        {
            var vibration = Math.Min(asset.BaseVibrationMmS + (emitIndex * 0.9), 16);
            var temp = Math.Min(asset.BaseTemperatureC + (emitIndex * 0.6), 118);
            return (hours, vibration, temp);
        }

        var jitterV = (NextJitter() - 0.5) * 0.3;
        var jitterT = (NextJitter() - 0.5) * 1.5;
        return (hours, Math.Max(0.5, asset.BaseVibrationMmS + jitterV), asset.BaseTemperatureC + jitterT);
    }

    private double NextJitter() => _random.NextDouble();
}

public sealed record AssetProfile(
    string AssetId,
    string EquipmentModel,
    double BaseHours,
    double BaseVibrationMmS,
    double BaseTemperatureC);
