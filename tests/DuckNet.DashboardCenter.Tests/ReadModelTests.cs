using DuckNet.Contracts;
using DuckNet.DashboardCenter;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter.Tests;

public class ReadModelTests
{
    [Fact]
    public void Hour_bucket_is_utc_truncated_to_the_hour()
    {
        var at = DateTimeOffset.Parse("2026-08-27T14:37:12+02:00");
        Assert.Equal("2026-08-27T12:00:00Z", DashboardReadModel.HourUtc(at));
    }

    [Fact]
    public void Upsert_increments_per_duck_per_hour()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var model = new DashboardReadModel();
        var hourA = DateTimeOffset.Parse("2026-08-27T12:10:00Z");
        var hourB = DateTimeOffset.Parse("2026-08-27T13:05:00Z");

        db.Write((conn, tx) =>
        {
            model.ApplySqueak(conn, tx, "duck-1", hourA);
            model.ApplySqueak(conn, tx, "duck-1", hourA.AddMinutes(20));
            model.ApplySqueak(conn, tx, "duck-1", hourB);
            model.ApplySqueak(conn, tx, "duck-2", hourA);
        });

        var rows = db.Read(conn => model.List(conn));
        Assert.Equal(3, rows.Count);
        Assert.Equal(new SqueakHourRow("duck-1", "2026-08-27T12:00:00Z", 2, 0), rows[0]);
        Assert.Equal(new SqueakHourRow("duck-1", "2026-08-27T13:00:00Z", 1, 0), rows[1]);
        Assert.Equal(new SqueakHourRow("duck-2", "2026-08-27T12:00:00Z", 1, 0), rows[2]);
        Assert.Equal(4, db.Read(conn => model.TotalCount(conn)));
    }

    [Fact]
    public void Empty_read_model_volume_sum_is_zero()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var model = new DashboardReadModel();
        Assert.Equal(0, db.Read(conn => model.TotalCount(conn)));
        Assert.Equal(0, db.Read(conn => model.TotalVolumeDb(conn)));
    }

    [Fact]
    public void Upsert_sums_volume_db_per_hour()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var model = new DashboardReadModel();
        var hour = DateTimeOffset.Parse("2026-08-27T12:10:00Z");

        db.Write((conn, tx) =>
        {
            model.ApplySqueak(conn, tx, "duck-1", hour, 70);
            model.ApplySqueak(conn, tx, "duck-1", hour.AddMinutes(20), 10);
        });

        var rows = db.Read(conn => model.List(conn));
        Assert.Equal(new SqueakHourRow("duck-1", "2026-08-27T12:00:00Z", 2, 80), rows[0]);
        Assert.Equal(80, db.Read(conn => model.TotalVolumeDb(conn)));
    }

    [Fact]
    public void Migration_adds_nullable_volume_db_to_step5_table()
    {
        using var db = KernelDb.OpenInMemory("""
            CREATE TABLE squeaks_by_duck_hour (
              duck_id TEXT NOT NULL,
              hour_utc TEXT NOT NULL,
              count INTEGER NOT NULL,
              PRIMARY KEY (duck_id, hour_utc)
            );
            """);
        db.Write((conn, tx) => DashboardReadModel.EnsureVolumeColumn(conn, tx));
        var model = new DashboardReadModel();
        var hour = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        db.Write((conn, tx) => model.ApplySqueak(conn, tx, "duck-1", hour, 70));
        Assert.Equal(70, db.Read(conn => model.List(conn))[0].VolumeDb);
        db.Write((conn, tx) => DashboardReadModel.EnsureVolumeColumn(conn, tx));
    }

    [Fact]
    public async Task Duplicate_event_id_increments_once()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var inbox = new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup);
        var readModel = new DashboardReadModel();
        var bus = new InMemoryEventBus();
        var consumer = new DashboardConsumer(bus, db, inbox, offsets, readModel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = consumer.RunAsync(cts.Token);

        var at = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var envelope = SqueakedEnvelope.Create(new Squeaked("duck-1", 1, at)) with { LogOffset = 1 };
        await bus.PublishAsync(envelope);
        await bus.PublishAsync(envelope);

        await WaitUntilAsync(() => consumer.AttemptCount >= 2 && consumer.HandledCount == 1);

        Assert.Equal(1, db.Read(conn => readModel.TotalCount(conn)));
        Assert.Equal(1, inbox.DuplicateSkipCount);

        cts.Cancel();
        await IgnoreCancel(run);
    }

    [Fact]
    public async Task V1_envelope_projects_with_default_volume()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var inbox = new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup);
        var readModel = new DashboardReadModel();
        var bus = new InMemoryEventBus();
        var consumer = new DashboardConsumer(bus, db, inbox, offsets, readModel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = consumer.RunAsync(cts.Token);

        var at = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var envelope = SqueakedEnvelope.CreateV1(new SqueakedV1("duck-1", 1, at)) with { LogOffset = 1 };
        await bus.PublishAsync(envelope);

        await WaitUntilAsync(() => consumer.HandledCount == 1);

        var rows = db.Read(conn => readModel.List(conn));
        Assert.Equal(new SqueakHourRow("duck-1", "2026-08-27T12:00:00Z", 1, 0), rows[0]);

        cts.Cancel();
        await IgnoreCancel(run);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("condition was not met");
    }

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
