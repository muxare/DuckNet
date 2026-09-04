using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter.Tests;

public class HealthStoreTests
{
    [Fact]
    public void Scoring_emits_prediction_then_raises_once()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, threshold: 3, windowSeconds: 60);
        var at = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

        db.Write((conn, tx) =>
        {
            var quiet = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 1, at, 4000, 2, 70));
            Assert.Equal(AlarmTransition.None, store.TryScore(conn, tx, quiet, SensorReadingReportedEnvelope.Parse(quiet)));

            var hot = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 2, at.AddMinutes(1), 9200, 12, 110));
            Assert.Equal(AlarmTransition.Raised, store.TryScore(conn, tx, hot, SensorReadingReportedEnvelope.Parse(hot)));

            var stillHot = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 3, at.AddMinutes(2), 9200, 12.5, 111));
            Assert.Equal(AlarmTransition.None, store.TryScore(conn, tx, stillHot, SensorReadingReportedEnvelope.Parse(stillHot)));
        });

        db.Read(conn =>
        {
            var unpublished = outbox.Unpublished(conn, 20);
            Assert.Contains(unpublished, row => EnvelopeJson.Deserialize(row.PayloadJson).Type == "AssetHealthPredicted");
            Assert.Contains(unpublished, row => EnvelopeJson.Deserialize(row.PayloadJson).Type == "HealthAlertRaised");
            var raised = unpublished
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .First(e => e.Type == "HealthAlertRaised");
            var predicted = unpublished
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .Last(e => e.Type == "AssetHealthPredicted");
            Assert.Equal(raised.TraceId, predicted.TraceId);
            Assert.Equal("TRK-001", HealthAlertRaisedEnvelope.Parse(raised).AssetId);
            Assert.Single(store.List(conn));
            return 0;
        });
    }

    [Fact]
    public void Recovered_score_publishes_HealthAlertResolved_with_causation()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, threshold: 3, windowSeconds: 60);
        var at = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

        db.Write((conn, tx) =>
        {
            var hot = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 1, at, 9200, 12, 110));
            Assert.Equal(AlarmTransition.Raised, store.TryScore(conn, tx, hot, SensorReadingReportedEnvelope.Parse(hot)));
            var quiet = SensorReadingReportedEnvelope.Create(
                new SensorReadingReported("TRK-001", 2, at.AddHours(1), 4000, 2, 70));
            Assert.Equal(AlarmTransition.Resolved, store.TryScore(conn, tx, quiet, SensorReadingReportedEnvelope.Parse(quiet)));
        });

        db.Read(conn =>
        {
            var unpublished = outbox.Unpublished(conn, 20)
                .Select(row => EnvelopeJson.Deserialize(row.PayloadJson))
                .ToList();
            var raised = unpublished.First(e => e.Type == "HealthAlertRaised");
            var resolved = unpublished.First(e => e.Type == "HealthAlertResolved");
            Assert.Equal(raised.EventId.ToString(), resolved.CausationId);
            return 0;
        });
    }
}
