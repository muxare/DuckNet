using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;

namespace DuckNet.Kernel.Tests;

public class EdgeBufferTests
{
    [Fact]
    public async Task Outage_holds_device_time_until_flush()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        var edge = new EdgeBufferStore();
        var log = new EventLogStore();
        var uplink = new UplinkGate();
        uplink.TakeDown();
        var publisher = new TransactionalPublisher(db, new StateStore(), new OutboxStore(), edge);
        var at = DateTimeOffset.Parse("2026-09-04T10:15:00Z");

        for (var i = 0; i < 8; i++)
        {
            await publisher.PublishSensorReadingAsync("TRK-001", 9000, 3 + i, 80, at.AddMinutes(i));
        }

        var dispatcher = new UplinkDispatcher(db, edge, log, uplink);
        Assert.Equal(0, dispatcher.FlushOnce());
        Assert.Equal(0, db.Read(conn => log.Count(conn)));
        Assert.Equal(8, db.Read(conn => edge.UnflushedCount(conn)));

        uplink.Restore();
        Assert.Equal(8, dispatcher.FlushOnce());

        var rows = db.Read(conn => log.ReadAfter(conn, 0, 20));
        Assert.Equal(8, rows.Count);
        Assert.All(rows, row => Assert.Equal("SensorReadingReported", row.Type));
        Assert.Equal(at, SensorReadingReportedEnvelope.Parse(rows[0]).OccurredAt);
        Assert.Equal(8, SensorReadingReportedEnvelope.Parse(rows[7]).SequenceNumber);
    }
}

public class EventLogPartitionTests
{
    [Fact]
    public void Partition_filter_keeps_owned_keys_together()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        var log = new EventLogStore(partitionCount: 4);
        db.Write((conn, tx) =>
        {
            log.Append(conn, tx, SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 1, DateTimeOffset.UtcNow, 1, 1, 70)));
            log.Append(conn, tx, SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("DRL-001", 1, DateTimeOffset.UtcNow, 1, 1, 70)));
        });

        var partA = log.PartitionOf("TRK-001");
        var partB = log.PartitionOf("DRL-001");
        var a = db.Read(conn => log.ReadAfter(conn, 0, 10, partA));
        Assert.Contains(a, e => e.PartitionKey == "TRK-001");
        if (partA != partB)
        {
            Assert.DoesNotContain(a, e => e.PartitionKey == "DRL-001");
        }
    }
}

public class LogExportTests
{
    [Fact]
    public void Export_matches_appended_envelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-export-{Guid.NewGuid():N}.ndjson");
        try
        {
            using var db = KernelDb.OpenInMemory(CenterSchema.Telemetry);
            var sink = new LogExportSink(path);
            var log = new EventLogStore(export: sink);
            var envelope = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 1, DateTimeOffset.UtcNow, 1, 2, 70));
            db.Write((conn, tx) => log.Append(conn, tx, envelope));
            var exported = sink.ReadFrom(0);
            Assert.Single(exported);
            Assert.Equal(envelope.EventId, exported[0].EventId);
            Assert.True(exported[0].LogOffset > 0);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

public class EventLogMigrationTests
{
    [Fact]
    public void Open_adds_log_partition_to_pre_orepart_event_log()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-oldlog-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE event_log (
                      offset INTEGER PRIMARY KEY AUTOINCREMENT,
                      event_id TEXT NOT NULL UNIQUE,
                      partition_key TEXT NOT NULL,
                      type TEXT NOT NULL,
                      version INTEGER NOT NULL,
                      sequence_number INTEGER NOT NULL,
                      payload_json TEXT NOT NULL,
                      occurred_at TEXT NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using var db = KernelDb.Open(path, CenterSchema.Telemetry);
            var log = new EventLogStore(partitionCount: 4);
            db.Write((conn, tx) => log.Append(
                conn,
                tx,
                SensorReadingReportedEnvelope.Create(
                    new SensorReadingReported("TRK-001", 1, DateTimeOffset.UtcNow, 1, 1, 70))));
            Assert.Single(db.Read(conn => log.ReadAfter(conn, 0, 10)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
