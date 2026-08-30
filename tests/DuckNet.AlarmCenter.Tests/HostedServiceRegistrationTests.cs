using DuckNet.EventBus;
using DuckNet.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DuckNet.AlarmCenter.Tests;

public class HostedServiceRegistrationTests
{
    [Fact]
    public async Task Feeder_consumer_and_dispatcher_are_all_registered_as_hosted_services()
    {
        var alarmDb = Path.Combine(Path.GetTempPath(), $"ducknet-alm-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(alarmDb);

        await using var alarm = AlarmApp.Create([], new AlarmOptions(
            DatabasePath: alarmDb,
            ResetDatabase: true,
            EventLogUrl: "http://127.0.0.1:1/",
            RateThreshold: 10,
            WindowSeconds: 60,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));

        // Three run loops: HttpLogTailFeeder, AlarmConsumer, RemoteOutboxDispatcher.
        // AddHostedService(factory) would dedupe these to one; see RunLoopHostedService.
        var runLoops = alarm.Services.GetServices<IHostedService>()
            .OfType<RunLoopHostedService>()
            .Count();
        Assert.Equal(3, runLoops);
    }
}
