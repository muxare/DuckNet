using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter.Tests;

public class CommerceProjectionTests
{
    [Fact]
    public async Task Projects_prediction_parts_and_confirmed_order()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var bus = new InMemoryEventBus();
        var consumer = new DashboardConsumer(
            bus,
            db,
            new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db),
            new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup),
            new DashboardReadModel(),
            shardCount: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var at = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
        var alarmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await bus.PublishAsync(SensorReadingReportedEnvelope.Create(
            new SensorReadingReported("TRK-001", 1, at, 9200, 12, 110)) with
        { LogOffset = 1 }, cts.Token);
        await bus.PublishAsync(AssetHealthPredictedEnvelope.Create(
            new AssetHealthPredicted("TRK-001", 0.82, at.AddHours(40), "Replace drive-axle bearing kit", 12, 110, 9200),
            sequenceNumber: 1) with
        { LogOffset = 2 }, cts.Token);
        await bus.PublishAsync(PartsReservedEnvelope.Create(
            new PartsReserved(alarmId, "TRK-001", [new PartLine("BRG-797-KIT", 1, 125000)], 125000, at.AddMinutes(5)),
            sequenceNumber: 2) with
        { LogOffset = 3 }, cts.Token);
        await bus.PublishAsync(OrderConfirmedEnvelope.Create(
            new OrderConfirmed(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), alarmId, "TRK-001",
                [new PartLine("BRG-797-KIT", 1, 125000)], 125000, at.AddMinutes(1)),
            sequenceNumber: 3,
            causationId: alarmId.ToString()) with
        { LogOffset = 4 }, cts.Token);

        await WaitUntilAsync(() => consumer.HandledCount >= 4, cts.Token);

        db.Read(conn =>
        {
            var fleet = new CommerceReadModel().ListFleet(conn);
            Assert.Single(fleet);
            Assert.Equal("TRK-001", fleet[0].AssetId);
            Assert.Equal(0.82, fleet[0].Score);

            var cases = new CommerceReadModel().ListServiceCases(conn);
            Assert.Equal("Confirmed", Assert.Single(cases).State);

            var orders = new CommerceReadModel().ListOrders(conn);
            Assert.Equal(alarmId, Assert.Single(orders).AlarmId);
            Assert.Equal(1, new DashboardReadModel().TotalCount(conn));
            return 0;
        });
    }

    [Fact]
    public async Task Duplicate_event_id_does_not_double_project()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var bus = new InMemoryEventBus();
        var consumer = new DashboardConsumer(
            bus,
            db,
            new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db),
            new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup),
            new DashboardReadModel(),
            shardCount: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var predicted = AssetHealthPredictedEnvelope.Create(
            new AssetHealthPredicted("TRK-001", 0.5, DateTimeOffset.UtcNow, "Inspect hydraulic cooling circuit", 4, 80, 5000),
            1) with
        { LogOffset = 1 };

        await bus.PublishAsync(predicted, cts.Token);
        await bus.PublishAsync(predicted, cts.Token);
        await WaitUntilAsync(() => consumer.AttemptCount >= 2, cts.Token);

        db.Read(conn =>
        {
            Assert.Single(new CommerceReadModel().ListFleet(conn));
            return 0;
        });
    }

    private static async Task WaitUntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!done())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
