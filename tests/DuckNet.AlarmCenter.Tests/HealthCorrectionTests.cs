using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter.Tests;

public class HealthCorrectionTests
{
    [Fact]
    public void Correcting_a_hot_reading_to_quiet_resolves()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, threshold: 3, windowSeconds: 60);
        var at = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
        var hot = SensorReadingReportedEnvelope.Create(
            new SensorReadingReported("TRK-001", 1, at, 9200, 12, 110));

        db.Write((conn, tx) =>
        {
            Assert.Equal(AlarmTransition.Raised, store.TryScore(conn, tx, hot, SensorReadingReportedEnvelope.Parse(hot)));
            var corrected = SensorReadingCorrectedEnvelope.Create(
                new SensorReadingCorrected("TRK-001", 1, hot.EventId, at, 4000, 2, 70));
            Assert.Equal(
                AlarmTransition.Resolved,
                store.TryCorrect(conn, tx, corrected, SensorReadingCorrectedEnvelope.Parse(corrected)));
        });

        db.Read(conn =>
        {
            var types = outbox.Unpublished(conn, 20)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson).Type)
                .ToList();
            Assert.Contains("HealthAlertRaised", types);
            Assert.Contains("HealthAlertResolved", types);
            return 0;
        });
    }
}

public class HealthShadowTests
{
    [Fact]
    public void Shadow_v2_does_not_raise_a_second_alert()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, 3, 60, HealthModels.V1, shadow: true);
        var at = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
        var hot = SensorReadingReportedEnvelope.Create(
            new SensorReadingReported("TRK-001", 1, at, 9200, 12, 110));

        db.Write((conn, tx) =>
        {
            Assert.Equal(AlarmTransition.Raised, store.TryScore(conn, tx, hot, SensorReadingReportedEnvelope.Parse(hot)));
        });

        db.Read(conn =>
        {
            var unpublished = outbox.Unpublished(conn, 20)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .ToList();
            var predicted = unpublished.Where(e => e.Type == "AssetHealthPredicted").ToList();
            Assert.Equal(2, predicted.Count);
            var versions = predicted.Select(e => AssetHealthPredictedEnvelope.Parse(e).ModelVersion).ToHashSet();
            Assert.Contains(HealthModels.V1, versions);
            Assert.Contains(HealthModels.V2, versions);
            Assert.Single(unpublished, e => e.Type == "HealthAlertRaised");
            return 0;
        });
    }
}
