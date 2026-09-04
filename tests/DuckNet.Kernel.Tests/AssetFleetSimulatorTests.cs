using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;

namespace DuckNet.Kernel.Tests;

public class AssetFleetSimulatorTests
{
    [Fact]
    public void Same_seed_emits_the_same_asset_sequence()
    {
        var first = CollectAssetIds(seed: 7, count: 12);
        var second = CollectAssetIds(seed: 7, count: 12);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Degraded_asset_vibration_climbs()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        var publisher = new TransactionalPublisher(db, new StateStore(), new OutboxStore());
        var fleet = new AssetFleetSimulator(publisher, seed: 1, minDelayMs: 0, maxDelayMs: 0);
        var early = fleet.Sample(AssetFleetSimulator.DefaultDegradedAssetId, 0);
        var late = fleet.Sample(AssetFleetSimulator.DefaultDegradedAssetId, 16);
        Assert.True(late.VibrationMmS > early.VibrationMmS + 5);
        Assert.True(late.TemperatureC > early.TemperatureC);
    }

    [Fact]
    public void Publish_writes_SensorReadingReported()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        var outbox = new OutboxStore();
        var publisher = new TransactionalPublisher(db, new StateStore(), outbox);
        var fleet = new AssetFleetSimulator(publisher, seed: 1, minDelayMs: 0, maxDelayMs: 0);
        fleet.PublishOneAsync("TRK-001").GetAwaiter().GetResult();

        db.Read(conn =>
        {
            var envelope = EnvelopeJson.Deserialize(outbox.Unpublished(conn, 1)[0].PayloadJson);
            Assert.Equal("SensorReadingReported", envelope.Type);
            Assert.Equal("TRK-001", envelope.PartitionKey);
            var reading = SensorReadingReportedEnvelope.Parse(envelope);
            Assert.Equal(1, reading.SequenceNumber);
            return 0;
        });
    }

    private static IReadOnlyList<string> CollectAssetIds(int seed, int count)
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        var outbox = new OutboxStore();
        var publisher = new TransactionalPublisher(db, new StateStore(), outbox);
        var fleet = new AssetFleetSimulator(publisher, seed: seed, minDelayMs: 0, maxDelayMs: 0);
        for (var i = 0; i < count; i++)
        {
            fleet.PublishNextAsync().GetAwaiter().GetResult();
        }

        return db.Read(conn =>
        {
            var rows = outbox.Unpublished(conn, 50);
            return rows.Select(row => EnvelopeJson.Deserialize(row.PayloadJson).PartitionKey).ToList();
        });
    }
}
