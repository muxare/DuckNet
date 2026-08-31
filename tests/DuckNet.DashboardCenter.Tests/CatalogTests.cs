using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.DashboardCenter;
using DuckNet.Kernel;

namespace DuckNet.DashboardCenter.Tests;

public class CatalogTests
{
    [Fact]
    public async Task Catalog_returns_configured_center_bases()
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
            Urls: "http://127.0.0.1:0",
            UiTelemetryUrl: "http://127.0.0.1:9001/",
            UiAlarmUrl: "http://127.0.0.1:9002/",
            UiBillingUrl: "http://127.0.0.1:9003/"));
        await dashboard.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(EnsureSlash(dashboard.Urls.First())) };
        var catalog = await http.GetFromJsonAsync<JsonElement>("/ui/catalog");
        Assert.Equal("http://127.0.0.1:9001", catalog.GetProperty("telemetry").GetString());
        Assert.Equal("http://127.0.0.1:9002", catalog.GetProperty("alarm").GetString());
        Assert.Equal("http://127.0.0.1:9003", catalog.GetProperty("billing").GetString());
        Assert.Equal("", catalog.GetProperty("dashboard").GetString());

        await dashboard.StopAsync();
    }

    [Fact]
    public async Task Catalog_is_empty_when_ui_urls_are_unset()
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
        var catalog = await http.GetFromJsonAsync<JsonElement>("/ui/catalog");
        Assert.Equal("", catalog.GetProperty("telemetry").GetString());
        Assert.Equal("", catalog.GetProperty("alarm").GetString());
        Assert.Equal("", catalog.GetProperty("billing").GetString());
        Assert.Equal("", catalog.GetProperty("dashboard").GetString());

        await dashboard.StopAsync();
    }

    [Fact]
    public void FromConfiguration_reads_ui_url_args()
    {
        var options = DashboardOptions.FromConfiguration([
            "--EVENT_LOG_URL=http://127.0.0.1:1/",
            "--UI_TELEMETRY_URL=http://tel.test/",
            "--UI_ALARM_URL=http://alm.test/",
            "--UI_BILLING_URL=http://bil.test/",
        ]);
        Assert.Equal("http://tel.test/", options.UiTelemetryUrl);
        Assert.Equal("http://alm.test/", options.UiAlarmUrl);
        Assert.Equal("http://bil.test/", options.UiBillingUrl);
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
