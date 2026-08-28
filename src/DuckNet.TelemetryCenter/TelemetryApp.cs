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
        var log = new EventLogStore();
        var publisher = new TransactionalPublisher(db, state, outbox);
        var simulator = new DuckSimulator(
            publisher,
            opts.DuckCount,
            opts.Seed,
            opts.MinDelayMs,
            opts.MaxDelayMs,
            opts.LoudDuckId);
        var dispatcher = new OutboxDispatcher(db, outbox, log);

        if (opts.InjectPoisonEvent)
        {
            db.Write((conn, tx) => log.Append(conn, tx, PoisonEvents.MalformedSqueaked()));
        }

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton(simulator);
        builder.Services.AddSingleton(dispatcher);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = EnvelopeJson.Options.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddHostedService<OutboxDispatcherHostedService>();
        if (opts.RunSimulator)
        {
            builder.Services.AddHostedService<SimulatorHostedService>();
        }

        var app = builder.Build();
        app.MapGet("/", () => Results.Redirect("/stats"));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "telemetry" }));
        app.MapGet("/bus/events", (long after, int? limit, KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var rows = kernelDb.Read(conn => eventLog.ReadAfter(conn, after, limit ?? 100));
            return Results.Json(rows, EnvelopeJson.Options);
        });
        app.MapPost("/bus/events", (EventEnvelope envelope, KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var offset = kernelDb.Write((conn, tx) => eventLog.Append(conn, tx, envelope));
            return Results.Json(new { offset });
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

            await pub.PublishSqueakAsync(request.DuckId, request.VolumeDb ?? 60, ct);
            return Results.Accepted();
        });
        app.MapGet("/stats", (KernelDb kernelDb, EventLogStore eventLog) =>
        {
            var count = kernelDb.Read(conn => eventLog.Count(conn));
            var max = kernelDb.Read(conn => eventLog.MaxOffset(conn));
            return Results.Json(new { logCount = count, lastOffset = max, database = kernelDb.DataSource });
        });

        return app;
    }
}

public sealed record IngestSqueakRequest(string DuckId, double? VolumeDb = null);

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
    string? LoudDuckId = null)
{
    public static TelemetryOptions FromConfiguration(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

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
            LoudDuckId: string.IsNullOrWhiteSpace(config["LOUD_DUCK_ID"]) ? null : config["LOUD_DUCK_ID"]);
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
