using System.Diagnostics;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.AlarmCenter.Tests;

public class TracingTests
{
    [Fact]
    public void AlarmRaised_copies_trace_id_and_sets_causation_to_parent_event_id()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var outbox = new OutboxStore();
        var store = new AlarmStore(outbox, threshold: 2, windowSeconds: 60);
        var at = DateTimeOffset.UtcNow;
        var parentTrace = "00-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee-ffffffffffffffff-01";
        Guid? parentEventId = null;

        db.Write((conn, tx) =>
        {
            for (var seq = 1; seq <= 3; seq++)
            {
                var envelope = SqueakedEnvelope.Create(
                    new Squeaked("duck-1", seq, at.AddSeconds(seq)),
                    traceId: parentTrace);
                if (store.TryRaise(conn, tx, envelope, SqueakedEnvelope.Parse(envelope)) == AlarmTransition.Raised)
                {
                    parentEventId = envelope.EventId;
                }
            }
        });

        var raised = db.Read(conn => EnvelopeJson.Deserialize(outbox.Unpublished(conn, 1)[0].PayloadJson));
        Assert.Equal("AlarmRaised", raised.Type);
        Assert.Equal(parentTrace, raised.TraceId);
        Assert.Equal(parentEventId?.ToString(), raised.CausationId);
        Assert.NotEqual(raised.EventId.ToString(), raised.CausationId);
    }

    [Fact]
    public async Task Handle_span_joins_the_squeak_trace_and_tags_duplicates()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Alarm);
        var inbox = new Inbox(AlarmConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, AlarmConsumer.ConsumerGroup);
        var bus = new InMemoryEventBus();
        var consumer = new AlarmConsumer(
            bus,
            db,
            inbox,
            offsets,
            new AlarmStore(new OutboxStore(), threshold: 10, windowSeconds: 60),
            sequencer: null);

        using var catcher = new ActivityCatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.UtcNow),
            traceId: "00-11111111111111111111111111111111-2222222222222222-01") with
        {
            LogOffset = 1
        };

        await bus.PublishAsync(envelope, cts.Token);
        await bus.PublishAsync(envelope, cts.Token);
        await WaitUntilAsync(() => consumer.AttemptCount >= 2, cts.Token);

        var hex = DuckNetTracing.TraceIdHex(envelope.TraceId);
        var handles = catcher.Stopped
            .Where(a => a.Source.Name == DuckNetTracing.AlarmSourceName
                && a.OperationName == "handle.Squeaked"
                && a.TraceId.ToHexString() == hex)
            .ToList();

        Assert.Equal(2, handles.Count);
        Assert.Equal(1, consumer.HandledCount);
        Assert.Contains(handles, a => Equals(a.GetTagItem(DuckNetTracing.TagDuplicate), true));
        Assert.All(handles, a => Assert.Equal(AlarmConsumer.ConsumerGroup, a.GetTagItem(DuckNetTracing.TagConsumerGroup)));
        Assert.All(handles, a => Assert.Equal("duck-1", a.GetBaggageItem(DuckNetTracing.BaggageDuckId)));
    }

    [Fact]
    public async Task TelemetryApp_hooks_activity_sources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ducknet-otel-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(path);
        await using var app = TelemetryApp.Create([], new TelemetryOptions(
            DatabasePath: path,
            ResetDatabase: true,
            RunSimulator: false,
            DuckCount: 1,
            Seed: 1,
            MinDelayMs: 10,
            MaxDelayMs: 10,
            Urls: "http://127.0.0.1:0"));
        await app.StartAsync();

        using var activity = DuckNetTracing.StartProducer(DuckNetTracing.Telemetry, "probe", "duck-1");
        Assert.NotNull(activity);
        await app.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!done())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}

internal sealed class ActivityCatcher : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];
    private readonly object _gate = new();

    public ActivityCatcher()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("DuckNet.", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Stopped
    {
        get
        {
            lock (_gate)
            {
                return [.. _stopped];
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
