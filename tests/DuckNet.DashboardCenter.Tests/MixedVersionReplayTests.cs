using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.Contracts;
using DuckNet.DashboardCenter;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.DashboardCenter.Tests;

public class MixedVersionReplayTests
{
    [Fact]
    public async Task Mixed_v1_v2_log_projects_volume_and_survives_rebuild()
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

        using var telemetryHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(telemetryUrl)) };
        const int eventCount = 10;
        const double v2Volume = 70;
        var at = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        for (var seq = 1; seq <= eventCount; seq++)
        {
            var envelope = seq % 2 == 1
                ? SqueakedEnvelope.CreateV1(new SqueakedV1("duck-1", seq, at.AddSeconds(seq)))
                : SqueakedEnvelope.Create(new Squeaked("duck-1", seq, at.AddSeconds(seq), v2Volume));
            var response = await telemetryHttp.PostAsJsonAsync("/bus/events", envelope, EnvelopeJson.Options);
            response.EnsureSuccessStatusCode();
        }

        await WaitUntilAsync(async () =>
        {
            var stats = await telemetryHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("logCount").GetInt64() >= eventCount;
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

        using var dashboardHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(dashboardUrl)) };
        const double expectedVolume = 5 * v2Volume;
        await WaitUntilAsync(async () =>
        {
            var stats = await dashboardHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("totalSqueaks").GetInt64() == eventCount
                && stats.GetProperty("totalVolumeDb").GetDouble() == expectedVolume;
        }, timeoutMs: 20_000);

        var snapshot = await dashboardHttp.GetFromJsonAsync<JsonElement>("/dashboard/summary");
        Assert.Equal(eventCount, snapshot.GetProperty("totalSqueaks").GetInt64());
        Assert.Equal(expectedVolume, snapshot.GetProperty("totalVolumeDb").GetDouble());

        var rebuild = await dashboardHttp.PostAsync("/dashboard/rebuild", content: null);
        rebuild.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () =>
        {
            var stats = await dashboardHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("totalSqueaks").GetInt64() == eventCount
                && stats.GetProperty("totalVolumeDb").GetDouble() == expectedVolume;
        }, timeoutMs: 20_000);

        var rebuilt = await dashboardHttp.GetFromJsonAsync<JsonElement>("/dashboard/summary");
        Assert.Equal(snapshot.GetProperty("rows").GetRawText(), rebuilt.GetProperty("rows").GetRawText());

        await dashboard.StopAsync();
        await telemetry.StopAsync();
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
