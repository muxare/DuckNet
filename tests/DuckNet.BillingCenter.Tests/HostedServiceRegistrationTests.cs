using DuckNet.EventBus;
using DuckNet.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DuckNet.BillingCenter.Tests;

public class HostedServiceRegistrationTests
{
    [Fact]
    public async Task Feeder_consumer_dispatcher_and_timeout_are_all_registered()
    {
        var billingDb = Path.Combine(Path.GetTempPath(), $"ducknet-bill-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(billingDb);

        await using var billing = BillingApp.Create([], new BillingOptions(
            DatabasePath: billingDb,
            ResetDatabase: true,
            EventLogUrl: "http://127.0.0.1:1/",
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));

        var runLoops = billing.Services.GetServices<IHostedService>()
            .OfType<RunLoopHostedService>()
            .Count();
        Assert.Equal(4, runLoops);
    }
}
