using DuckNet.EventBus;
using DuckNet.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DuckNet.DashboardCenter.Tests;

public class HostedServiceRegistrationTests
{
    [Fact]
    public async Task Feeder_and_consumer_are_both_registered_as_hosted_services()
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

        // Two run loops: HttpLogTailFeeder and DashboardConsumer.
        // AddHostedService(factory) would dedupe these to one; see RunLoopHostedService.
        var runLoops = dashboard.Services.GetServices<IHostedService>()
            .OfType<RunLoopHostedService>()
            .Count();
        Assert.Equal(2, runLoops);
    }
}
