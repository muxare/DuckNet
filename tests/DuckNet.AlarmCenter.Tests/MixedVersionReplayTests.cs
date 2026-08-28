using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.AlarmCenter;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.AlarmCenter.Tests;

public class MixedVersionReplayTests
{
    [Fact]
    public async Task Mixed_v1_v2_log_replays_without_handler_changes()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        var alarmDb = Path.Combine(Path.GetTempPath(), $"ducknet-alm-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);
        KernelRunner.DeleteSqliteFiles(alarmDb);

        await using var telemetry = TelemetryApp.Create([], new TelemetryOptions(
            DatabasePath: telemetryDb,
            ResetDatabase: true,
            RunSimulator: false,
            DuckCount: 1,
            Seed: 1,
            MinDelayMs: 10,
            MaxDelayMs: 10,
            Urls: "http://127.0.0.1:0"));
        await telemetry.StartAsync();
        var telemetryUrl = telemetry.Urls.First();

        using var http = new HttpClient { BaseAddress = new Uri(EnsureSlash(telemetryUrl)) };
        var at = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        const int eventCount = 12;
        for (var seq = 1; seq <= eventCount; seq++)
        {
            var envelope = seq % 2 == 1
                ? SqueakedEnvelope.CreateV1(new SqueakedV1("duck-1", seq, at.AddSeconds(seq)))
                : SqueakedEnvelope.Create(new Squeaked("duck-1", seq, at.AddSeconds(seq), 80));
            var response = await http.PostAsJsonAsync("/bus/events", envelope, EnvelopeJson.Options);
            response.EnsureSuccessStatusCode();
        }

        await WaitUntilAsync(async () =>
        {
            var stats = await http.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("logCount").GetInt64() >= eventCount;
        });

        await using var alarm = AlarmApp.Create([], new AlarmOptions(
            DatabasePath: alarmDb,
            ResetDatabase: true,
            EventLogUrl: telemetryUrl,
            RateThreshold: 10,
            WindowSeconds: 60,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));
        await alarm.StartAsync();
        var alarmUrl = alarm.Urls.First();

        using var alarmHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(alarmUrl)) };
        await WaitUntilAsync(async () =>
        {
            var stats = await alarmHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("alarmCount").GetInt32() >= 1;
        });

        var alarmStats = await alarmHttp.GetFromJsonAsync<JsonElement>("/stats");
        Assert.Equal(alarmDb, alarmStats.GetProperty("database").GetString());
        Assert.NotEqual(telemetryDb, alarmStats.GetProperty("database").GetString());

        await alarm.StopAsync();
        await telemetry.StopAsync();

        using var alarmSqlite = KernelDb.Open(alarmDb, CenterSchema.Alarm);
        Assert.True(alarmSqlite.Read(conn => new AlarmStore(new OutboxStore(), 10, 60).List(conn).Count) >= 1);
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(last?.Message ?? "condition was not met");
    }
}
