using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.DashboardCenter;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.DashboardCenter.Tests;

public class RebuildTests
{
    [Fact]
    public async Task Rebuild_after_truncate_matches_pre_delete_snapshot()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        var dashboardDb = Path.Combine(Path.GetTempPath(), $"ducknet-dash-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);
        KernelRunner.DeleteSqliteFiles(dashboardDb);

        await using var telemetry = TelemetryApp.Create([], new TelemetryOptions(
            DatabasePath: telemetryDb,
            ResetDatabase: true,
            RunSimulator: false,
            DuckCount: 5,
            Seed: 1,
            MinDelayMs: 10,
            MaxDelayMs: 10,
            Urls: "http://127.0.0.1:0"));
        await telemetry.StartAsync();
        var telemetryUrl = telemetry.Urls.First();

        using var telemetryHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(telemetryUrl)) };
        const int eventCount = 1000;
        for (var i = 0; i < eventCount; i++)
        {
            var duckId = $"duck-{(i % 5) + 1}";
            var response = await telemetryHttp.PostAsJsonAsync("/ingest/squeak", new { duckId });
            response.EnsureSuccessStatusCode();
        }

        await WaitUntilAsync(async () =>
        {
            var stats = await telemetryHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("logCount").GetInt64() >= eventCount;
        }, timeoutMs: 20_000);

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

        using var dashboardHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(dashboardUrl)) };
        await WaitUntilAsync(async () =>
        {
            var stats = await dashboardHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("totalSqueaks").GetInt64() == eventCount;
        }, timeoutMs: 20_000);

        var snapshot = await dashboardHttp.GetFromJsonAsync<JsonElement>("/dashboard/summary");
        Assert.Equal(eventCount, snapshot.GetProperty("totalSqueaks").GetInt64());

        var statsBefore = await dashboardHttp.GetFromJsonAsync<JsonElement>("/stats");
        Assert.Equal(dashboardDb, statsBefore.GetProperty("database").GetString());
        Assert.NotEqual(telemetryDb, statsBefore.GetProperty("database").GetString());

        var telemetryBefore = await telemetryHttp.GetFromJsonAsync<JsonElement>("/stats");
        var telemetryLogBefore = telemetryBefore.GetProperty("logCount").GetInt64();

        var rebuild = await dashboardHttp.PostAsync("/dashboard/rebuild", content: null);
        rebuild.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () =>
        {
            var stats = await dashboardHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("totalSqueaks").GetInt64() == eventCount;
        }, timeoutMs: 20_000);

        var rebuilt = await dashboardHttp.GetFromJsonAsync<JsonElement>("/dashboard/summary");
        Assert.Equal(snapshot.GetProperty("totalSqueaks").GetInt64(), rebuilt.GetProperty("totalSqueaks").GetInt64());
        Assert.Equal(snapshot.GetProperty("rowCount").GetInt32(), rebuilt.GetProperty("rowCount").GetInt32());
        Assert.Equal(snapshot.GetProperty("rows").GetRawText(), rebuilt.GetProperty("rows").GetRawText());

        var telemetryAfter = await telemetryHttp.GetFromJsonAsync<JsonElement>("/stats");
        Assert.Equal(telemetryLogBefore, telemetryAfter.GetProperty("logCount").GetInt64());
        Assert.Equal(telemetryDb, telemetryAfter.GetProperty("database").GetString());

        await dashboard.StopAsync();
        await telemetry.StopAsync();

        using var telemetrySqlite = KernelDb.Open(telemetryDb, CenterSchema.Telemetry);
        using var dashboardSqlite = KernelDb.Open(dashboardDb, CenterSchema.Dashboard);
        Assert.Contains("event_log", telemetrySqlite.TableNames());
        Assert.DoesNotContain("event_log", dashboardSqlite.TableNames());
        Assert.Contains("squeaks_by_duck_hour", dashboardSqlite.TableNames());
        Assert.Equal(eventCount, dashboardSqlite.Read(conn => new DashboardReadModel().TotalCount(conn)));
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
