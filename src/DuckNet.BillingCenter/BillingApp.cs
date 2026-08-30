using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter;

public static class BillingApp
{
    public static WebApplication Create(string[] args, BillingOptions? options = null)
    {
        var opts = options ?? BillingOptions.FromConfiguration(args);
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

        var db = KernelDb.Open(opts.DatabasePath, CenterSchema.Billing);
        var inbox = new Inbox(BillingConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, BillingConsumer.ConsumerGroup);
        var outbox = new OutboxStore();
        var sagaTimeout = opts.SagaTimeout ?? TimeSpan.FromMinutes(5);
        var timeoutPoll = opts.TimeoutPollInterval ?? TimeSpan.FromMilliseconds(500);
        var sagas = new BillingStore(outbox, opts.FeeAmountCents, sagaTimeout);
        var lastSeq = db.Read(conn => sagas.LoadAlarmSeq(conn));
        var sequencer = new PerKeySequencer(lastSeq);
        var time = TimeProvider.System;

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
        builder.Services.AddSingleton(sagas);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(new DeadLetterStore());
        builder.Services.AddSingleton(inner);
        builder.Services.AddSingleton<IEventBus>(duplicator);
        builder.Services.AddSingleton(duplicator);
        builder.Services.AddSingleton(shuffler);
        builder.Services.AddSingleton(sequencer);
        builder.Services.AddSingleton(sp => new BillingConsumer(
            inner,
            db,
            inbox,
            offsets,
            sagas,
            sequencer,
            deadLetters: sp.GetRequiredService<DeadLetterStore>(),
            time: time,
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
        builder.Services.AddSingleton(sp => new SagaTimeoutWorker(
            db,
            sagas,
            time,
            timeoutPoll));

        // AddSingleton<IHostedService>, not AddHostedService: the factory overload of
        // AddHostedService dedupes by implementation type and would keep only the first
        // RunLoopHostedService registration.
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<HttpLogTailFeeder>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<BillingConsumer>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<RemoteOutboxDispatcher>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<SagaTimeoutWorker>().RunAsync));

        var app = builder.Build();
        app.MapGet("/", () => Results.Redirect("/sagas"));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "billing" }));
        app.MapGet("/sagas", (KernelDb kernelDb, BillingStore store) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            return Results.Json(rows);
        });
        app.MapGet("/dlq", (KernelDb kernelDb, DeadLetterStore dlq) =>
        {
            var rows = kernelDb.Read(conn => dlq.List(conn, BillingConsumer.ConsumerGroup));
            return Results.Json(rows);
        });
        app.MapPost("/dlq/{id:long}/replay", (long id, bool fix, BillingConsumer consumer) =>
        {
            return consumer.TryReplay(id, fix)
                ? Results.Ok(new { id, status = "replayed", fix })
                : Results.NotFound();
        });
        app.MapPost("/dlq/{id:long}/skip", (long id, BillingConsumer consumer) =>
        {
            return consumer.TrySkip(id)
                ? Results.Ok(new { id, status = "skipped" })
                : Results.NotFound();
        });
        app.MapGet("/stats", (KernelDb kernelDb, BillingStore store, ConsumerOffsetStore offsetStore, DeadLetterStore dlq, BillingConsumer consumer, SagaTimeoutWorker timeout) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            var dlqCount = kernelDb.Read(conn => dlq.Count(conn, BillingConsumer.ConsumerGroup));
            var shards = consumer.ShardSnapshot;
            return Results.Json(new
            {
                sagaCount = rows.Count,
                reserved = rows.Count(r => r.State == BillingStore.StateReserved),
                released = rows.Count(r => r.State == BillingStore.StateReleased),
                expired = rows.Count(r => r.State == BillingStore.StateExpired),
                lastOffset = offsetStore.LastOffset,
                database = kernelDb.DataSource,
                feeAmountCents = store.AmountCents,
                sagaTimeoutSeconds = store.Timeout.TotalSeconds,
                timeoutExpiredCount = timeout.ExpiredCount,
                reservedCount = consumer.ReservedCount,
                releasedCount = consumer.ReleasedCount,
                dlqCount,
                shardCount = shards?.Shards.Count ?? opts.ShardCount,
                shards = shards?.Shards,
                keys = shards?.Keys
            });
        });
        app.MapGet("/metrics", (BillingConsumer consumer) =>
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

public sealed record BillingOptions(
    string DatabasePath,
    bool ResetDatabase,
    string EventLogUrl,
    double DuplicateRate,
    bool ShuffleEnabled,
    int ShuffleWindow,
    string? Urls,
    int FeeAmountCents = 100,
    TimeSpan? SagaTimeout = null,
    TimeSpan? TimeoutPollInterval = null,
    int ShardCount = PartitionShard.DefaultCount,
    int HandleDelayMs = 0,
    int ShardCapacity = PartitionShard.DefaultCapacity)
{
    public static BillingOptions FromConfiguration(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var eventLogUrl = config["EVENT_LOG_URL"]
            ?? config["services:telemetry:http:0"]
            ?? "";

        return new BillingOptions(
            DatabasePath: config["DUCKNET_DB"] ?? "billing.db",
            ResetDatabase: IsTrue(config["RESET_DB"]),
            EventLogUrl: eventLogUrl,
            DuplicateRate: ParseRate(config["DUPLICATE_RATE"], 0.15),
            ShuffleEnabled: !IsFalse(config["SHUFFLE_ENABLED"]),
            ShuffleWindow: ParseInt(config["SHUFFLE_WINDOW"], 50),
            Urls: config["URLS"],
            FeeAmountCents: ParseInt(config["BILLING_FEE_CENTS"], 100),
            SagaTimeout: TimeSpan.FromSeconds(ParseInt(config["SAGA_TIMEOUT_SECONDS"], 300)),
            TimeoutPollInterval: TimeSpan.FromMilliseconds(ParseInt(config["SAGA_TIMEOUT_POLL_MS"], 500)),
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
