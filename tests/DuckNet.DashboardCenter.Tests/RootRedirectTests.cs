using System.Net;
using DuckNet.DashboardCenter;
using DuckNet.Kernel;

namespace DuckNet.DashboardCenter.Tests;

public class RootRedirectTests
{
    [Fact]
    public async Task Json_summary_is_still_on_the_api_path()
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

        using var http = new HttpClient
        {
            BaseAddress = new Uri(EnsureSlash(dashboard.Urls.First()))
        };
        var response = await http.GetAsync("/dashboard/summary");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        Assert.Contains("json", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("totalSqueaks", body, StringComparison.Ordinal);

        var home = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("DuckNet", html, StringComparison.Ordinal);

        await dashboard.StopAsync();
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
