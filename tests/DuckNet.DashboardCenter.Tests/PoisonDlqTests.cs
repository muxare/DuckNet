using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.DashboardCenter;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.DashboardCenter.Tests;

public class PoisonDlqTests
{
    [Fact]
    public async Task Poison_event_is_dead_lettered_and_hour_buckets_still_fill()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        var dashboardDb = Path.Combine(Path.GetTempPath(), $"ducknet-dash-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);
        KernelRunner.DeleteSqliteFiles(dashboardDb);

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
        for (var i = 0; i < 4; i++)
        {
            (await http.PostAsJsonAsync("/ingest/squeak", new { duckId = "duck-1" })).EnsureSuccessStatusCode();
        }

        (await http.PostAsJsonAsync("/bus/poison", new { })).EnsureSuccessStatusCode();

        for (var i = 0; i < 4; i++)
        {
            (await http.PostAsJsonAsync("/ingest/squeak", new { duckId = "duck-1" })).EnsureSuccessStatusCode();
        }

        await WaitUntilAsync(async () =>
        {
            var stats = await http.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("logCount").GetInt64() >= 9;
        });

        await using var dashboard = DashboardApp.Create([], new DashboardOptions(
            DatabasePath: dashboardDb,
            ResetDatabase: true,
            EventLogUrl: telemetryUrl,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));
        await dashboard.StartAsync();
        var dashboardUrl = dashboard.Urls.First();

        using var dashHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(dashboardUrl)) };
        await WaitUntilAsync(async () =>
        {
            var stats = await dashHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("totalSqueaks").GetInt64() >= 8
                && stats.GetProperty("dlqCount").GetInt32() >= 1;
        });

        var dlq = await dashHttp.GetFromJsonAsync<JsonElement>("/dlq");
        Assert.True(dlq.GetArrayLength() >= 1);
        var id = dlq[0].GetProperty("id").GetInt64();
        Assert.Contains("JsonException", dlq[0].GetProperty("error").GetString(), StringComparison.Ordinal);

        var skip = await dashHttp.PostAsync($"/dlq/{id}/skip", null);
        skip.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () =>
        {
            var stats = await dashHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("dlqCount").GetInt32() == 0;
        });

        var summary = await dashHttp.GetFromJsonAsync<JsonElement>("/dashboard/summary");
        Assert.Equal(8, summary.GetProperty("totalSqueaks").GetInt64());

        using var dashSqlite = KernelDb.Open(dashboardDb, CenterSchema.Dashboard);
        Assert.Contains("dead_letter_queue", dashSqlite.TableNames());
        Assert.DoesNotContain("event_log", dashSqlite.TableNames());

        await dashboard.StopAsync();
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
