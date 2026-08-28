using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.AlarmCenter;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.AlarmCenter.Tests;

public class PoisonDlqTests
{
    [Fact]
    public async Task Poison_event_is_dead_lettered_and_stream_still_raises()
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
        for (var i = 0; i < 6; i++)
        {
            (await http.PostAsJsonAsync("/ingest/squeak", new { duckId = "duck-1" })).EnsureSuccessStatusCode();
        }

        var poison = await http.PostAsJsonAsync("/bus/poison", new { });
        poison.EnsureSuccessStatusCode();

        for (var i = 0; i < 6; i++)
        {
            (await http.PostAsJsonAsync("/ingest/squeak", new { duckId = "duck-1" })).EnsureSuccessStatusCode();
        }

        await WaitUntilAsync(async () =>
        {
            var stats = await http.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("logCount").GetInt64() >= 13;
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
            return stats.GetProperty("alarmCount").GetInt32() >= 1
                && stats.GetProperty("dlqCount").GetInt32() >= 1;
        });

        var dlq = await alarmHttp.GetFromJsonAsync<JsonElement>("/dlq");
        Assert.True(dlq.GetArrayLength() >= 1);
        var row = dlq[0];
        Assert.Contains("JsonException", row.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains(PoisonEvents.PayloadJson, row.GetProperty("payloadJson").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, row.GetProperty("attempts").GetInt32());

        var id = row.GetProperty("id").GetInt64();
        var replay = await alarmHttp.PostAsync($"/dlq/{id}/replay?fix=true", null);
        replay.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () =>
        {
            var stats = await alarmHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("dlqCount").GetInt32() == 0;
        });

        using var alarmSqlite = KernelDb.Open(alarmDb, CenterSchema.Alarm);
        Assert.Contains("dead_letter_queue", alarmSqlite.TableNames());
        Assert.DoesNotContain("event_log", alarmSqlite.TableNames());

        await alarm.StopAsync();
        await telemetry.StopAsync();
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 15000)
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
