using DuckNet.DashboardCenter;
using DuckNet.Kernel;
using DuckNet.TelemetryCenter;

namespace DuckNet.DashboardCenter.Tests;

public class LabCorsTests
{
    [Fact]
    public async Task Dashboard_stats_allows_browser_origin()
    {
        var dashboardDb = Path.Combine(Path.GetTempPath(), $"ducknet-dash-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(dashboardDb);

        await using var dashboard = DashboardApp.Create([], new DashboardOptions(
            DatabasePath: dashboardDb,
            ResetDatabase: true,
            EventLogUrl: "http://127.0.0.1:1/",
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));
        await dashboard.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(EnsureSlash(dashboard.Urls.First())) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stats");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Contains("*", values);

        await dashboard.StopAsync();
    }

    [Fact]
    public async Task Telemetry_stats_allows_browser_origin()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);

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

        using var http = new HttpClient { BaseAddress = new Uri(EnsureSlash(telemetry.Urls.First())) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stats");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Contains("*", values);

        await telemetry.StopAsync();
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
