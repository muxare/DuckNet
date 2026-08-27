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
            sequencer));
        builder.Services.AddSingleton(sp => new HttpLogTailFeeder(
            sp.GetRequiredService<HttpLogClient>(),
            duplicator,
            startOffset: offsets.LastOffset));
        builder.Services.AddSingleton(sp => new RemoteOutboxDispatcher(
            db,
            outbox,
            sp.GetRequiredService<HttpLogClient>()));

        builder.Services.AddHostedService<AlarmFeederHostedService>();
        builder.Services.AddHostedService<AlarmConsumerHostedService>();
        builder.Services.AddHostedService<RemoteOutboxDispatcherHostedService>();

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok", center = "alarm" }));
        app.MapGet("/alarms", (KernelDb kernelDb, AlarmStore store) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            return Results.Json(rows);
        });
        app.MapGet("/stats", (KernelDb kernelDb, AlarmStore store, ConsumerOffsetStore offsetStore) =>
        {
            var rows = kernelDb.Read(conn => store.List(conn));
            return Results.Json(new
            {
                alarmCount = rows.Count,
                lastOffset = offsetStore.LastOffset,
                database = kernelDb.DataSource,
                threshold = store.Threshold,
                windowSeconds = store.WindowSeconds
            });
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
    string? Urls)
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
            Urls: config["URLS"]);
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
