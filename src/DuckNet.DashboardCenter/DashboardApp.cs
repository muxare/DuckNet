using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter;

public static class DashboardApp
{
    public static WebApplication Create(string[] args, DashboardOptions? options = null)
    {
        var opts = options ?? DashboardOptions.FromConfiguration(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(opts.EventLogUrl);

        var wwwroot = FindWwwRoot();
        var builder = wwwroot is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                WebRootPath = wwwroot,
            });
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

        var db = KernelDb.Open(opts.DatabasePath, CenterSchema.Dashboard);
        db.Write((conn, tx) => DashboardReadModel.EnsureVolumeColumn(conn, tx));
        var inbox = new Inbox(DashboardConsumer.ConsumerGroup, enabled: true, db);
        var offsets = new ConsumerOffsetStore(db, DashboardConsumer.ConsumerGroup);
        var readModel = new DashboardReadModel();

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
        builder.Services.AddSingleton(readModel);
        builder.Services.AddSingleton(new DeadLetterStore());
        builder.Services.AddSingleton(inner);
        builder.Services.AddSingleton<IEventBus>(duplicator);
        builder.Services.AddSingleton(duplicator);
        builder.Services.AddSingleton(shuffler);
        builder.Services.AddSingleton(sp => new HttpLogTailFeeder(
            sp.GetRequiredService<HttpLogClient>(),
            duplicator,
            startOffset: offsets.LastOffset));
        builder.Services.AddSingleton(sp => new DashboardConsumer(
            inner,
            db,
            inbox,
            offsets,
            readModel,
            sp.GetRequiredService<HttpLogTailFeeder>(),
            deadLetters: sp.GetRequiredService<DeadLetterStore>(),
            shardCount: opts.ShardCount,
            handleDelay: TimeSpan.FromMilliseconds(opts.HandleDelayMs),
            shardCapacity: opts.ShardCapacity));

        // AddSingleton<IHostedService>, not AddHostedService: the factory overload of
        // AddHostedService dedupes by implementation type and would keep only the first
        // RunLoopHostedService registration.
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<HttpLogTailFeeder>().RunAsync));
        builder.Services.AddSingleton<IHostedService>(sp => new RunLoopHostedService(sp.GetRequiredService<DashboardConsumer>().RunAsync));

        var app = builder.Build();
        if (wwwroot is not null)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "dashboard" }));
        app.MapGet("/dashboard/summary", (KernelDb kernelDb, DashboardReadModel model) =>
        {
            var rows = kernelDb.Read(conn => model.List(conn));
            var total = kernelDb.Read(conn => model.TotalCount(conn));
            var volume = kernelDb.Read(conn => model.TotalVolumeDb(conn));
            return Results.Json(new DashboardSummary(rows, total, rows.Count, volume));
        });
        app.MapGet("/dashboard/duck/{id}", (string id, KernelDb kernelDb, DashboardReadModel model) =>
        {
            var rows = kernelDb.Read(conn => model.List(conn, id));
            return Results.Json(rows);
        });
        app.MapPost("/dashboard/rebuild", async (DashboardConsumer consumer, CancellationToken ct) =>
        {
            await consumer.RebuildAsync(ct);
            return Results.Accepted(value: new { status = "replaying" });
        });
        app.MapGet("/dlq", (KernelDb kernelDb, DeadLetterStore dlq) =>
        {
            var rows = kernelDb.Read(conn => dlq.List(conn, DashboardConsumer.ConsumerGroup));
            return Results.Json(rows);
        });
        app.MapPost("/dlq/{id:long}/replay", (long id, bool fix, DashboardConsumer consumer) =>
        {
            return consumer.TryReplay(id, fix)
                ? Results.Ok(new { id, status = "replayed", fix })
                : Results.NotFound();
        });
        app.MapPost("/dlq/{id:long}/skip", (long id, DashboardConsumer consumer) =>
        {
            return consumer.TrySkip(id)
                ? Results.Ok(new { id, status = "skipped" })
                : Results.NotFound();
        });
        app.MapGet("/stats", (KernelDb kernelDb, DashboardReadModel model, ConsumerOffsetStore offsetStore, DeadLetterStore dlq, DashboardConsumer consumer) =>
        {
            var total = kernelDb.Read(conn => model.TotalCount(conn));
            var volume = kernelDb.Read(conn => model.TotalVolumeDb(conn));
            var rows = kernelDb.Read(conn => model.List(conn));
            var dlqCount = kernelDb.Read(conn => dlq.Count(conn, DashboardConsumer.ConsumerGroup));
            var shards = consumer.ShardSnapshot;
            return Results.Json(new
            {
                totalSqueaks = total,
                totalVolumeDb = volume,
                rowCount = rows.Count,
                lastOffset = offsetStore.LastOffset,
                database = kernelDb.DataSource,
                dlqCount,
                shardCount = shards?.Shards.Count ?? opts.ShardCount,
                shards = shards?.Shards,
                keys = shards?.Keys
            });
        });
        app.MapGet("/metrics", (DashboardConsumer consumer) =>
        {
            var snapshot = consumer.ShardSnapshot
                ?? new ShardMetricsSnapshot(Array.Empty<ShardSnapshot>(), Array.Empty<KeyLagSnapshot>());
            return Results.Json(snapshot);
        });

        return app;
    }

    private static string? FindWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var nested = Path.Combine(dir.FullName, "src", "DuckNet.DashboardCenter", "wwwroot");
            if (File.Exists(Path.Combine(nested, "index.html")))
            {
                return nested;
            }

            var local = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(local, "index.html")))
            {
                return local;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}

public sealed record DashboardSummary(
    IReadOnlyList<SqueakHourRow> Rows,
    long TotalSqueaks,
    int RowCount,
    double TotalVolumeDb);

public sealed record DashboardOptions(
    string DatabasePath,
    bool ResetDatabase,
    string EventLogUrl,
    double DuplicateRate,
    bool ShuffleEnabled,
    int ShuffleWindow,
    string? Urls,
    int ShardCount = PartitionShard.DefaultCount,
    int HandleDelayMs = 0,
    int ShardCapacity = PartitionShard.DefaultCapacity)
{
    public static DashboardOptions FromConfiguration(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var eventLogUrl = config["EVENT_LOG_URL"]
            ?? config["services:telemetry:http:0"]
            ?? "";

        return new DashboardOptions(
            DatabasePath: config["DUCKNET_DB"] ?? "dashboard.db",
            ResetDatabase: IsTrue(config["RESET_DB"]),
            EventLogUrl: eventLogUrl,
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
