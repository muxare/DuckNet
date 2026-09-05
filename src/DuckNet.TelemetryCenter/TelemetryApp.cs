using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.Kernel.Producer;

namespace DuckNet.TelemetryCenter;

public static class TelemetryApp
{
    public static WebApplication Create(string[] args, TelemetryOptions? options = null)
    {
        var opts = options ?? TelemetryOptions.FromConfiguration(args);
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        if (!string.IsNullOrWhiteSpace(opts.Urls))
        {
            builder.WebHost.UseUrls(opts.Urls);
        }

        if (opts.ResetDatabase)
        {
            KernelRunner.DeleteSqliteFiles(opts.DatabasePath);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(opts.DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var db = KernelDb.Open(opts.DatabasePath, CenterSchema.Telemetry);
        var state = new StateStore();
        var outbox = new OutboxStore();
        var exportPath = opts.ExportPath
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.DatabasePath)) ?? ".", "event-log.ndjson");
        var export = new LogExportSink(exportPath);
        var log = new EventLogStore(opts.LogPartitionCount, export);
        var edge = new EdgeBufferStore();
        var uplink = new UplinkGate();
        if (opts.UplinkOutageSeconds > 0)
        {
            uplink.TakeDown(TimeSpan.FromSeconds(opts.UplinkOutageSeconds));
        }

        var publisher = new TransactionalPublisher(db, state, outbox, edge);
        ITelemetrySimulator simulator = opts.FleetSimulator
            ? new AssetFleetSimulator(
                publisher,
                opts.AssetCount,
                opts.Seed,
                opts.MinDelayMs,
                opts.MaxDelayMs,
                opts.DegradedAssetId,
                activitySource: DuckNetTracing.Telemetry)
            : new DuckSimulator(
                publisher,
                opts.DuckCount,
                opts.Seed,
                opts.MinDelayMs,
                opts.MaxDelayMs,
                opts.LoudDuckId,
                activitySource: DuckNetTracing.Telemetry);
        var dispatcher = new OutboxDispatcher(db, outbox, log, DuckNetTracing.Telemetry);
        var uplinkDispatcher = new UplinkDispatcher(db, edge, log, uplink);

        if (opts.InjectPoisonEvent)
        {
            db.Write((conn, tx) => log.Append(conn, tx, PoisonEvents.MalformedSqueaked()));
        }

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<ITelemetrySimulator>(simulator);
        builder.Services.AddSingleton(dispatcher);
        builder.Services.AddSingleton(uplinkDispatcher);
        builder.Services.AddSingleton(uplink);
        builder.Services.AddSingleton(edge);
        builder.Services.AddSingleton(export);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = EnvelopeJson.Options.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        // AddSingleton<IHostedService>, not AddHostedService: the factory overload of
        // AddHostedService dedupes by implementation type and would drop any second
        // RunLoopHostedService registration.
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<OutboxDispatcher>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<UplinkDispatcher>().RunAsync));
        if (opts.RunSimulator)
        {
            builder.Services.AddHostedService<SimulatorHostedService>();
        }

        var app = builder.Build();
        app.UseDuckNetLabCors();
        app.MapGet("/", () => Results.Redirect("/stats"));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "telemetry" }));
        app.MapGet("/bus/events", (long after, int? limit, int? partition, KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var rows = kernelDb.Read(conn => eventLog.ReadAfter(conn, after, limit ?? 100, partition));
            return Results.Json(rows, EnvelopeJson.Options);
        });
        app.MapPost("/bus/events", (EventEnvelope envelope, KernelDb kernelDb, EventLogStore eventLog, TelemetryOptions options) =>
        {
            if (!IsAllowed(envelope, options, eventLog))
            {
                return Results.BadRequest(new { error = "unknown-tenant" });
            }

            var offset = kernelDb.Write((conn, tx) => eventLog.Append(conn, tx, envelope));
            return Results.Json(new { offset });
        });
        app.MapPost("/bus/events/batch", (List<EventEnvelope> envelopes, KernelDb kernelDb, EventLogStore eventLog, TelemetryOptions options) =>
        {
            foreach (var envelope in envelopes)
            {
                if (!IsAllowed(envelope, options, eventLog))
                {
                    return Results.BadRequest(new { error = "unknown-tenant", eventId = envelope.EventId });
                }
            }

            var offsets = kernelDb.Write((conn, tx) => eventLog.AppendBatch(conn, tx, envelopes));
            return Results.Json(new { offsets });
        });
        app.MapGet("/bus/export", (long fromOffset, int? limit, LogExportSink sink) =>
        {
            var rows = sink.ReadFrom(fromOffset, limit ?? 1000);
            return Results.Json(rows, EnvelopeJson.Options);
        });
        app.MapPost("/bus/poison", (KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var envelope = PoisonEvents.MalformedSqueaked();
            var offset = kernelDb.Write((conn, tx) => eventLog.Append(conn, tx, envelope));
            return Results.Json(new { offset, eventId = envelope.EventId, partitionKey = envelope.PartitionKey });
        });
        app.MapPost("/ingest/squeak", async (IngestSqueakRequest request, TransactionalPublisher pub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DuckId))
            {
                return Results.BadRequest();
            }

            using var activity = DuckNetTracing.StartProducer(DuckNetTracing.Telemetry, "ingest.squeak", request.DuckId);
            await pub.PublishSqueakAsync(request.DuckId, request.VolumeDb ?? 60, ct);
            return Results.Accepted();
        });
        app.MapPost("/ingest/reading", async (IngestReadingRequest request, TransactionalPublisher pub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.AssetId))
            {
                return Results.BadRequest();
            }

            using var activity = DuckNetTracing.StartProducer(DuckNetTracing.Telemetry, "ingest.reading", request.AssetId);
            await pub.PublishSensorReadingAsync(
                request.AssetId,
                request.EngineHours,
                request.VibrationMmS,
                request.TemperatureC,
                occurredAt: request.OccurredAt,
                tenantId: request.TenantId,
                ct);
            return Results.Accepted();
        });
        app.MapPost("/ingest/correction", async (IngestCorrectionRequest request, TransactionalPublisher pub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.AssetId) || request.SupersedesEventId == Guid.Empty)
            {
                return Results.BadRequest();
            }

            using var activity = DuckNetTracing.StartProducer(DuckNetTracing.Telemetry, "ingest.correction", request.AssetId);
            await pub.PublishCorrectionAsync(
                new SensorReadingCorrected(
                    request.AssetId,
                    request.SequenceNumber,
                    request.SupersedesEventId,
                    request.OccurredAt,
                    request.EngineHours,
                    request.VibrationMmS,
                    request.TemperatureC,
                    request.TenantId ?? OrePartTenants.Default),
                ct);
            return Results.Accepted();
        });
        app.MapPost("/ingest/uplink/down", (UplinkGate gate, int? seconds) =>
        {
            gate.TakeDown(seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null);
            return Results.Ok(new { uplink = "down" });
        });
        app.MapPost("/ingest/uplink/up", (UplinkGate gate) =>
        {
            gate.Restore();
            return Results.Ok(new { uplink = "up" });
        });
        app.MapGet("/ingest/uplink", (UplinkGate gate, KernelDb kernelDb, EdgeBufferStore buffer) =>
        {
            var pending = kernelDb.Read(conn => buffer.UnflushedCount(conn));
            return Results.Json(new { up = gate.IsUp, pending });
        });
        app.MapGet("/stats", (KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var count = kernelDb.Read(conn => eventLog.Count(conn));
            var max = kernelDb.Read(conn => eventLog.MaxOffset(conn));
            return Results.Json(new
            {
                logCount = count,
                lastOffset = max,
                database = kernelDb.DataSource,
                partitionCount = eventLog.PartitionCount
            });
        });

        return app;
    }

    private static bool IsAllowed(EventEnvelope envelope, TelemetryOptions options, EventLogStore log)
    {
        _ = log;
        if (!string.Equals(envelope.Type, "SensorReadingReported", StringComparison.Ordinal)
            && !string.Equals(envelope.Type, "SensorReadingCorrected", StringComparison.Ordinal))
        {
            return true;
        }

        var current = EventUpcasterPipeline.Default.Upcast(envelope);
        var tenant = string.Equals(current.Type, "SensorReadingCorrected", StringComparison.Ordinal)
            ? SensorReadingCorrectedEnvelope.Parse(current).TenantId
            : SensorReadingReportedEnvelope.Parse(current).TenantId;
        return options.TenantAllowList.Contains(tenant);
    }
}

public sealed record IngestSqueakRequest(string DuckId, double? VolumeDb = null);

public sealed record IngestReadingRequest(
    string AssetId,
    double EngineHours,
    double VibrationMmS,
    double TemperatureC,
    DateTimeOffset? OccurredAt = null,
    string? TenantId = null);

public sealed record IngestCorrectionRequest(
    string AssetId,
    long SequenceNumber,
    Guid SupersedesEventId,
    DateTimeOffset OccurredAt,
    double EngineHours,
    double VibrationMmS,
    double TemperatureC,
    string? TenantId = null);

public sealed record TelemetryOptions(
    string DatabasePath,
    bool ResetDatabase,
    bool RunSimulator,
    int DuckCount,
    int? Seed,
    int MinDelayMs,
    int MaxDelayMs,
    string? Urls,
    bool InjectPoisonEvent = false,
    string? LoudDuckId = null,
    bool FleetSimulator = false,
    int AssetCount = 8,
    string? DegradedAssetId = null,
    int UplinkOutageSeconds = 0,
    int LogPartitionCount = 1,
    string? ExportPath = null,
    IReadOnlySet<string>? AllowedTenants = null)
{
    public IReadOnlySet<string> TenantAllowList =>
        AllowedTenants is { Count: > 0 }
            ? AllowedTenants
            : new HashSet<string>(StringComparer.Ordinal) { OrePartTenants.Default };

    public static TelemetryOptions FromConfiguration(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var tenants = ParseTenants(config["TELEMETRY_TENANTS"]);
        return new TelemetryOptions(
            DatabasePath: config["DUCKNET_DB"] ?? "telemetry.db",
            ResetDatabase: IsTrue(config["RESET_DB"]),
            RunSimulator: !IsFalse(config["RUN_SIMULATOR"]),
            DuckCount: ParseInt(config["DUCK_COUNT"], 5),
            Seed: ParseNullableInt(config["SIMULATOR_SEED"]) ?? 42,
            MinDelayMs: ParseInt(config["SQUEAK_MIN_DELAY_MS"], 20),
            MaxDelayMs: ParseInt(config["SQUEAK_MAX_DELAY_MS"], 80),
            Urls: config["URLS"],
            InjectPoisonEvent: IsTrue(config["INJECT_POISON_EVENT"]),
            LoudDuckId: string.IsNullOrWhiteSpace(config["LOUD_DUCK_ID"]) ? null : config["LOUD_DUCK_ID"],
            FleetSimulator: IsTrue(config["FLEET_SIMULATOR"]),
            AssetCount: ParseInt(config["ASSET_COUNT"], 8),
            DegradedAssetId: string.IsNullOrWhiteSpace(config["DEGRADED_ASSET_ID"])
                ? AssetFleetSimulator.DefaultDegradedAssetId
                : config["DEGRADED_ASSET_ID"],
            UplinkOutageSeconds: ParseInt(config["UPLINK_OUTAGE_SECONDS"], 0),
            LogPartitionCount: Math.Max(1, ParseInt(config["LOG_PARTITION_COUNT"], 1)),
            ExportPath: string.IsNullOrWhiteSpace(config["LOG_EXPORT_PATH"]) ? null : config["LOG_EXPORT_PATH"],
            AllowedTenants: tenants);
    }

    private static IReadOnlySet<string> ParseTenants(string? value)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { OrePartTenants.Default };
        if (string.IsNullOrWhiteSpace(value))
        {
            return set;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part);
        }

        return set;
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;
}
