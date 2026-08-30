using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.AlarmCenter;

public static class AlarmApp
{
    public static WebApplication Create(string[] args, AlarmOptions? options = null)
    {
        var opts = options ?? AlarmOptions.FromConfiguration(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(opts.EventLogUrl);

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

        var db = KernelDb.Open(opts.DatabasePath, CenterSchema.Alarm);
        var inbox = new Inbox(AlarmConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, AlarmConsumer.ConsumerGroup);
        var outbox = new OutboxStore();
        var alarms = new AlarmStore(outbox, opts.RateThreshold, opts.WindowSeconds);
        var lastSeq = db.Read(conn => alarms.LoadSqueakSeq(conn));
        var sequencer = new PerKeySequencer(lastSeq);

        var inner = new InMemoryEventBus();
        var shuffler = new ShufflerMiddleware(inner, opts.ShuffleWindow, seed: 42, opts.ShuffleEnabled);
        var duplicator = new DuplicatorMiddleware(
            shuffler,
            opts.DuplicateRate,
            seed: 42,
            maxDelay: TimeSpan.FromMilliseconds(40));

        builder.Services.AddHttpClient<HttpLogClient>(client =>
        {
            client.BaseAddress = new Uri(EnsureTrailingSlash(opts.EventLogUrl));
        });

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton(inbox);
        builder.Services.AddSingleton(offsets);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(alarms);
        builder.Services.AddSingleton(new DeadLetterStore());
        builder.Services.AddSingleton(inner);
        builder.Services.AddSingleton<IEventBus>(duplicator);
        builder.Services.AddSingleton(duplicator);
        builder.Services.AddSingleton(shuffler);
        builder.Services.AddSingleton(sequencer);
        builder.Services.AddSingleton(sp => new AlarmConsumer(
            inner,
            db,
            inbox,
            offsets,
            alarms,
            sequencer,
            deadLetters: sp.GetRequiredService<DeadLetterStore>(),
            shardCount: opts.ShardCount,
            handleDelay: TimeSpan.FromMilliseconds(opts.HandleDelayMs),
            shardCapacity: opts.ShardCapacity));
        builder.Services.AddSingleton(sp => new HttpLogTailFeeder(
            sp.GetRequiredService<HttpLogClient>(),
            duplicator,
            startOffset: offsets.LastOffset));
        builder.Services.AddSingleton(sp => new RemoteOutboxDispatcher(
            db,
            outbox,
            sp.GetRequiredService<HttpLogClient>()));

        // AddSingleton<IHostedService>, not AddHostedService: the factory overload of
        // AddHostedService dedupes by implementation type and would keep only the first
        // RunLoopHostedService registration.
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<HttpLogTailFeeder>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<AlarmConsumer>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<RemoteOutboxDispatcher>().RunAsync));

        var app = builder.Build();
        app.MapGet("/", () => Results.Redirect("/alarms"));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "alarm" }));
        app.MapGet("/alarms", (KernelDb kernelDb, AlarmStore store) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            return Results.Json(rows);
        });
        app.MapGet("/dlq", (KernelDb kernelDb, DeadLetterStore dlq) =>
        {
            var rows = kernelDb.Read(conn => dlq.List(conn, AlarmConsumer.ConsumerGroup));
            return Results.Json(rows);
        });
        app.MapPost("/dlq/{id:long}/replay", (long id, bool fix, AlarmConsumer consumer) =>
        {
            return consumer.TryReplay(id, fix)
                ? Results.Ok(new { id, status = "replayed", fix })
                : Results.NotFound();
        });
        app.MapPost("/dlq/{id:long}/skip", (long id, AlarmConsumer consumer) =>
        {
            return consumer.TrySkip(id)
                ? Results.Ok(new { id, status = "skipped" })
                : Results.NotFound();
        });
        app.MapGet("/stats", (KernelDb kernelDb, AlarmStore store, ConsumerOffsetStore offsetStore, DeadLetterStore dlq, AlarmConsumer consumer) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            var dlqCount = kernelDb.Read(conn => dlq.Count(conn, AlarmConsumer.ConsumerGroup));
            var shards = consumer.ShardSnapshot;
            return Results.Json(new
            {
                alarmCount = rows.Count,
                lastOffset = offsetStore.LastOffset,
                database = kernelDb.DataSource,
                threshold = store.Threshold,
                windowSeconds = store.WindowSeconds,
                dlqCount,
                shardCount = shards?.Shards.Count ?? opts.ShardCount,
                shards = shards?.Shards,
                keys = shards?.Keys
            });
        });
        app.MapGet("/metrics", (AlarmConsumer consumer) =>
        {
            var snapshot = consumer.ShardSnapshot
                ?? new ShardMetricsSnapshot(Array.Empty<ShardSnapshot>(), Array.Empty<KeyLagSnapshot>());
            return Results.Json(snapshot);
        });

        return app;
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}

public sealed record AlarmOptions(
    string DatabasePath,
    bool ResetDatabase,
    string EventLogUrl,
    int RateThreshold,
    int WindowSeconds,
    double DuplicateRate,
    bool ShuffleEnabled,
    int ShuffleWindow,
    string? Urls,
    int ShardCount = PartitionShard.DefaultCount,
    int HandleDelayMs = 0,
    int ShardCapacity = PartitionShard.DefaultCapacity)
{
    public static AlarmOptions FromConfiguration(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var eventLogUrl = config["EVENT_LOG_URL"]
            ?? config["services:telemetry:http:0"]
            ?? "";

        return new AlarmOptions(
            DatabasePath: config["DUCKNET_DB"] ?? "alarm.db",
            ResetDatabase: IsTrue(config["RESET_DB"]),
            EventLogUrl: eventLogUrl,
            RateThreshold: ParseInt(config["ALARM_RATE_THRESHOLD"], 10),
            WindowSeconds: ParseInt(config["ALARM_WINDOW_SECONDS"], 60),
            DuplicateRate: ParseRate(config["DUPLICATE_RATE"], 0.15),
            ShuffleEnabled: !IsFalse(config["SHUFFLE_ENABLED"]),
            ShuffleWindow: ParseInt(config["SHUFFLE_WINDOW"], 50),
            Urls: config["URLS"],
            ShardCount: ParseInt(config["SHARD_COUNT"], PartitionShard.DefaultCount),
            HandleDelayMs: ParseInt(config["HANDLE_DELAY_MS"], 0),
            ShardCapacity: ParseInt(config["SHARD_CAPACITY"], PartitionShard.DefaultCapacity));
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double ParseRate(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= 0 and <= 1
            ? parsed
            : fallback;
}
