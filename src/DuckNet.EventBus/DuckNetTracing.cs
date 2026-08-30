using System.Diagnostics;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Envelope-carried trace context. Centers do not call each other, so W3C
/// <c>traceparent</c> travels on <see cref="EventEnvelope.TraceId"/> — not HTTP headers.
/// Replays keep the same value (duplicator clones the envelope); each delivery
/// still starts a span so the retry is visible. Inbox, not the tracer, is idempotent.
/// </summary>
public static class DuckNetTracing
{
    public const string TelemetrySourceName = "DuckNet.Telemetry";
    public const string AlarmSourceName = "DuckNet.Alarm";
    public const string DashboardSourceName = "DuckNet.Dashboard";
    public const string BillingSourceName = "DuckNet.Billing";
    public const string KernelSourceName = "DuckNet.Kernel";

    public static readonly ActivitySource Telemetry = new(TelemetrySourceName);
    public static readonly ActivitySource Alarm = new(AlarmSourceName);
    public static readonly ActivitySource Dashboard = new(DashboardSourceName);
    public static readonly ActivitySource Billing = new(BillingSourceName);
    public static readonly ActivitySource Kernel = new(KernelSourceName);

    public static readonly string[] SourceNames =
    [
        TelemetrySourceName,
        AlarmSourceName,
        DashboardSourceName,
        BillingSourceName,
        KernelSourceName
    ];

    public const string TagEventId = "ducknet.event_id";
    public const string TagPartitionKey = "ducknet.partition_key";
    public const string TagConsumerGroup = "ducknet.consumer_group";
    public const string TagEventType = "ducknet.event_type";
    public const string TagDuplicate = "ducknet.duplicate";
    public const string TagLogOffset = "ducknet.log_offset";
    public const string TagCausationId = "ducknet.causation_id";
    public const string BaggageDuckId = "duckId";

    public static Activity? StartProducer(ActivitySource source, string operation, string duckId)
    {
        var activity = source.StartActivity(operation, ActivityKind.Producer);
        activity?.SetTag(TagPartitionKey, duckId);
        activity?.SetBaggage(BaggageDuckId, duckId);
        return activity;
    }

    public static Activity? StartFromEnvelope(
        ActivitySource source,
        string operation,
        EventEnvelope envelope,
        ActivityKind kind = ActivityKind.Consumer,
        string? consumerGroup = null)
    {
        var parent = ParentContextFrom(envelope.TraceId);
        var activity = parent is { } ctx
            ? source.StartActivity(operation, kind, ctx)
            : source.StartActivity(operation, kind);
        Tag(activity, envelope, consumerGroup);
        return activity;
    }

    public static void MarkDuplicate(Activity? activity) =>
        activity?.SetTag(TagDuplicate, true);

    /// <summary>
    /// W3C <c>traceparent</c> from the current span, or a new root if none is running.
    /// The envelope always gets a value so a later consumer can join the same trace
    /// even when no <see cref="ActivityListener"/> was registered at publish time.
    /// </summary>
    public static string CurrentOrNewTraceParent()
    {
        if (Activity.Current?.Id is { Length: > 0 } id)
        {
            return id;
        }

        return $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";
    }

    public static string TraceIdHex(string? traceParentOrId)
    {
        return TryParseTraceId(traceParentOrId, out var traceId)
            ? traceId.ToHexString()
            : traceParentOrId ?? "";
    }

    public static bool TryParseTraceId(string? value, out ActivityTraceId traceId)
    {
        traceId = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (ActivityContext.TryParse(value, traceState: null, out var ctx) && ctx.TraceId != default)
        {
            traceId = ctx.TraceId;
            return true;
        }

        if (value.Length != 32)
        {
            return false;
        }

        try
        {
            traceId = ActivityTraceId.CreateFromString(value);
            return traceId != default;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static ActivityContext? ParentContextFrom(string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return null;
        }

        if (ActivityContext.TryParse(traceId, traceState: null, out var ctx) && ctx.TraceId != default)
        {
            return new ActivityContext(
                ctx.TraceId,
                ctx.SpanId,
                ctx.TraceFlags | ActivityTraceFlags.Recorded,
                ctx.TraceState,
                isRemote: true);
        }

        if (TryParseTraceId(traceId, out var parsed))
        {
            return new ActivityContext(parsed, default, ActivityTraceFlags.Recorded, isRemote: true);
        }

        return null;
    }

    private static void Tag(Activity? activity, EventEnvelope envelope, string? consumerGroup)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(TagEventId, envelope.EventId.ToString());
        activity.SetTag(TagPartitionKey, envelope.PartitionKey);
        activity.SetTag(TagEventType, envelope.Type);
        activity.SetTag(TagLogOffset, envelope.LogOffset);
        if (envelope.CausationId is not null)
        {
            activity.SetTag(TagCausationId, envelope.CausationId);
        }

        if (consumerGroup is not null)
        {
            activity.SetTag(TagConsumerGroup, consumerGroup);
        }

        activity.SetBaggage(BaggageDuckId, envelope.PartitionKey);
    }
}
