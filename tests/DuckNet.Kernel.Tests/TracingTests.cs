using System.Diagnostics;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;

namespace DuckNet.Kernel.Tests;

public class TracingTests
{
    [Fact]
    public async Task Publisher_stamps_traceparent_and_log_round_trips_it()
    {
        using var catcher = new ActivityCatcher();
        using var db = KernelDb.OpenInMemory();
        var outbox = new OutboxStore();
        var log = new EventLogStore();
        var publisher = new TransactionalPublisher(db, new StateStore(), outbox);

        using var activity = DuckNetTracing.StartProducer(DuckNetTracing.Kernel, "simulate.squeak", "duck-1");
        Assert.NotNull(activity);
        await publisher.PublishSqueakAsync("duck-1");
        await new OutboxDispatcher(db, outbox, log).DrainAsync();

        var row = db.Read(conn => log.ReadAfter(conn, 0, 1).Single());
        Assert.Equal(activity.TraceId.ToHexString(), DuckNetTracing.TraceIdHex(row.TraceId));
        Assert.StartsWith("00-", row.TraceId, StringComparison.Ordinal);
        Assert.Null(row.CausationId);
        Assert.Contains(catcher.Stopped, a => a.OperationName == "append.log"
            && a.TraceId == activity.TraceId);
    }

    [Fact]
    public async Task Duplicate_delivery_keeps_the_same_trace_id()
    {
        var inner = new InMemoryEventBus();
        var bus = new DuplicatorMiddleware(inner, duplicateRate: 1.0, seed: 1);
        var inbox = new Inbox("test");
        var counter = new SqueakCounter(bus, "test", inbox, sequencerEnabled: false);

        using var catcher = new ActivityCatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = counter.RunAsync(cts.Token);

        using var producer = DuckNetTracing.StartProducer(DuckNetTracing.Kernel, "simulate.squeak", "duck-1");
        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.UtcNow),
            traceId: DuckNetTracing.CurrentOrNewTraceParent());
        await bus.PublishAsync(envelope, cts.Token);
        await ConsumerWait.UntilAttemptsAsync(counter, expected: 2, cts.Token);

        var hex = DuckNetTracing.TraceIdHex(envelope.TraceId);
        var handles = catcher.Stopped
            .Where(a => a.Source.Name == DuckNetTracing.KernelSourceName
                && a.OperationName == "handle.Squeaked"
                && a.TraceId.ToHexString() == hex)
            .ToList();

        Assert.Equal(2, handles.Count);
        Assert.Equal(envelope.EventId.ToString(), handles[0].GetTagItem(DuckNetTracing.TagEventId));
        Assert.Contains(handles, a => Equals(a.GetTagItem(DuckNetTracing.TagDuplicate), true));
        Assert.Equal(1, counter.TotalCount);
    }

    [Fact]
    public void Upcaster_preserves_trace_and_causation()
    {
        var source = SqueakedEnvelope.CreateV1(
            new SqueakedV1("duck-1", 1, DateTimeOffset.Parse("2026-08-30T10:00:00Z")),
            traceId: "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
            causationId: Guid.NewGuid().ToString());

        var upcast = EventUpcasterPipeline.Default.Upcast(source);
        Assert.Equal(source.TraceId, upcast.TraceId);
        Assert.Equal(source.CausationId, upcast.CausationId);
        Assert.Equal(source.EventId, upcast.EventId);
    }

    [Fact]
    public void Existing_event_log_gains_nullable_trace_columns()
    {
        using var db = KernelDb.OpenInMemory("""
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
            """);

        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-1", 1, DateTimeOffset.UtcNow),
            traceId: "00-cccccccccccccccccccccccccccccccc-dddddddddddddddd-01");
        db.Write((conn, tx) => new EventLogStore().Append(conn, tx, envelope));
        var row = db.Read(conn => new EventLogStore().ReadAfter(conn, 0, 1).Single());
        Assert.Equal(envelope.TraceId, row.TraceId);
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
