using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter.Tests;

public class LateDataProjectionTests
{
    [Fact]
    public void Hour_bucket_uses_device_time_not_ingest_time()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var model = new DashboardReadModel();
        var deviceHour = DateTimeOffset.Parse("2026-09-04T10:15:00Z");
        db.Write((conn, tx) =>
        {
            for (var i = 0; i < 8; i++)
            {
                model.ApplyReading(conn, tx, Guid.NewGuid(), "TRK-001", i + 1, deviceHour.AddMinutes(i), 3);
            }
        });

        var rows = db.Read(conn => model.ListReadingHours(conn, "TRK-001"));
        Assert.Single(rows);
        Assert.Equal("2026-09-04T10:00:00Z", rows[0].HourUtc);
        Assert.Equal(8, rows[0].Count);
    }

    [Fact]
    public void Correction_reopens_a_closed_hour_and_replaces_vibration()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var model = new DashboardReadModel();
        var first = DateTimeOffset.Parse("2026-09-04T10:15:00Z");
        var laterHour = DateTimeOffset.Parse("2026-09-04T11:05:00Z");
        var eventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        db.Write((conn, tx) =>
        {
            model.ApplyReading(conn, tx, eventId, "TRK-001", 1, first, 12);
            model.ApplyReading(conn, tx, Guid.NewGuid(), "TRK-001", 2, laterHour, 3);
            model.ApplyCorrection(conn, tx, Guid.NewGuid(), "TRK-001", 1, first, 2);
        });

        var ten = db.Read(conn => model.ListReadingHours(conn, "TRK-001"))
            .Single(r => r.HourUtc == "2026-09-04T10:00:00Z");
        Assert.Equal(1, ten.Count);
        Assert.Equal(2, ten.VibrationSum);
        Assert.True(ten.ReopenCount >= 1);
    }
}

public class HealthCutoverTests
{
    [Fact]
    public void Live_queries_stay_on_v1_until_cutover()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var commerce = new CommerceReadModel();
        var at = DateTimeOffset.UtcNow;
        var v1 = new AssetHealthPredicted("TRK-001", 0.4, at, "Inspect", 2, 70, 4000, HealthModels.V1);
        var v2 = new AssetHealthPredicted("TRK-001", 0.9, at, "Replace", 12, 110, 9200, HealthModels.V2);
        db.Write((conn, tx) =>
        {
            commerce.ApplyPredicted(conn, tx, v1);
            commerce.ApplyPredicted(conn, tx, v2);
        });

        db.Read(conn =>
        {
            Assert.Equal(0.4, Assert.Single(commerce.ListFleet(conn)).Score);
            Assert.Equal(0.9, Assert.Single(commerce.ListShadow(conn)).Score);
            return 0;
        });

        db.Write((conn, tx) => commerce.CutoverHealth(conn, tx, HealthModels.V2));
        db.Read(conn =>
        {
            Assert.Equal(0.9, Assert.Single(commerce.ListFleet(conn)).Score);
            Assert.Equal(HealthModels.V2, commerce.LiveHealthModel(conn));
            return 0;
        });
    }
}

public class CatalogProjectionTests
{
    [Fact]
    public async Task Catalog_events_project_part_names()
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

        await bus.PublishAsync(PartCatalogPublishedEnvelope.Create(
            new PartCatalogPublished("BRG-797-KIT", "CAT 797 drive-axle bearing kit", 125000)) with
        { LogOffset = 1 }, cts.Token);
        await WaitUntilAsync(() => consumer.HandledCount >= 1, cts.Token);

        db.Read(conn =>
        {
            var part = Assert.Single(new CommerceReadModel().ListCatalogParts(conn));
            Assert.Equal("BRG-797-KIT", part.Sku);
            Assert.Contains("bearing", part.Name, StringComparison.OrdinalIgnoreCase);
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

public class TenantFilterTests
{
    [Fact]
    public void Fleet_list_is_scoped_by_tenant()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var commerce = new CommerceReadModel();
        var at = DateTimeOffset.UtcNow;
        db.Write((conn, tx) =>
        {
            commerce.ApplyPredicted(conn, tx, new AssetHealthPredicted("TRK-001", 0.8, at, "x", 12, 110, 9000));
            commerce.ApplyPredicted(conn, tx, new AssetHealthPredicted("TRK-002", 0.2, at, "y", 2, 70, 4000));
            commerce.ApplyTenant(conn, tx, "TRK-001", "acme");
            commerce.ApplyTenant(conn, tx, "TRK-002", "beta");
        });

        db.Read(conn =>
        {
            Assert.Equal("TRK-001", Assert.Single(commerce.ListFleet(conn, "acme")).AssetId);
            Assert.Equal("TRK-002", Assert.Single(commerce.ListFleet(conn, "beta")).AssetId);
            Assert.Equal(2, commerce.ListFleet(conn).Count);
            return 0;
        });
    }
}
