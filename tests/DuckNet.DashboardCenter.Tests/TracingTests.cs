using System.Diagnostics;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter.Tests;

public class TracingTests
{
    [Fact]
    public async Task Handle_span_joins_the_squeak_trace()
    {
        using var db = KernelDb.OpenInMemory(CenterSchema.Dashboard);
        var inbox = new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup);
        var bus = new InMemoryEventBus();
        var consumer = new DashboardConsumer(
            bus,
            db,
            inbox,
            offsets,
            new DashboardReadModel());

        using var catcher = new ActivityCatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = consumer.RunAsync(cts.Token);

        var envelope = SqueakedEnvelope.Create(
            new Squeaked("duck-2", 1, DateTimeOffset.UtcNow),
            traceId: "00-33333333333333333333333333333333-4444444444444444-01") with
        {
            LogOffset = 1
        };

        await bus.PublishAsync(envelope, cts.Token);
        await WaitUntilAsync(() => consumer.HandledCount >= 1, cts.Token);

        var hex = DuckNetTracing.TraceIdHex(envelope.TraceId);
        var handle = Assert.Single(catcher.Stopped, a =>
                a.Source.Name == DuckNetTracing.DashboardSourceName
                && a.OperationName == "handle.Squeaked"
                && a.TraceId.ToHexString() == hex);
        Assert.Equal(envelope.EventId.ToString(), handle.GetTagItem(DuckNetTracing.TagEventId));
        Assert.Equal(DashboardConsumer.ConsumerGroup, handle.GetTagItem(DuckNetTracing.TagConsumerGroup));
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
