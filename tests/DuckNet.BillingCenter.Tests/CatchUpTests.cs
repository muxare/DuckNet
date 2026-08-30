using System.Net.Http.Json;
using System.Text.Json;
using DuckNet.AlarmCenter;
using DuckNet.Contracts;
using DuckNet.EventBus;
using DuckNet.Kernel;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.BillingCenter.Tests;

public class CatchUpTests
{
    [Fact]
    public async Task BillingCenter_catches_up_AlarmRaised_from_the_log()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        var billingDb = Path.Combine(Path.GetTempPath(), $"ducknet-bill-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);
        KernelRunner.DeleteSqliteFiles(billingDb);

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
        var raised = AlarmRaisedEnvelope.Create(new AlarmRaised("duck-1", 12, DateTimeOffset.UtcNow), 1);
        var post = await http.PostAsJsonAsync("/bus/events", raised, EnvelopeJson.Options);
        post.EnsureSuccessStatusCode();

        await using var billing = BillingApp.Create([], new BillingOptions(
            DatabasePath: billingDb,
            ResetDatabase: true,
            EventLogUrl: telemetryUrl,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0",
            SagaTimeout: TimeSpan.FromMinutes(5)));
        await billing.StartAsync();
        var billingUrl = billing.Urls.First();

        using var billingHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(billingUrl)) };
        await WaitUntilAsync(async () =>
        {
            var stats = await billingHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("reserved").GetInt32() >= 1;
        });

        var stats = await billingHttp.GetFromJsonAsync<JsonElement>("/stats");
        Assert.Equal(billingDb, stats.GetProperty("database").GetString());
        Assert.NotEqual(telemetryDb, stats.GetProperty("database").GetString());

        await billing.StopAsync();
        await telemetry.StopAsync();

        using var billingSqlite = KernelDb.Open(billingDb, CenterSchema.Billing);
        Assert.DoesNotContain("event_log", billingSqlite.TableNames());
        Assert.Contains("billing_sagas", billingSqlite.TableNames());
        Assert.Equal(
            BillingStore.StateReserved,
            billingSqlite.Read(conn => new BillingStore(new OutboxStore(), 100, TimeSpan.FromMinutes(5)).Get(conn, raised.EventId)!.State));
    }

    [Fact]
    public async Task Fast_resolve_from_AlarmCenter_releases_the_fee()
    {
        var telemetryDb = Path.Combine(Path.GetTempPath(), $"ducknet-tel-{Guid.NewGuid():N}.db");
        var alarmDb = Path.Combine(Path.GetTempPath(), $"ducknet-alm-{Guid.NewGuid():N}.db");
        var billingDb = Path.Combine(Path.GetTempPath(), $"ducknet-bill-{Guid.NewGuid():N}.db");
        KernelRunner.DeleteSqliteFiles(telemetryDb);
        KernelRunner.DeleteSqliteFiles(alarmDb);
        KernelRunner.DeleteSqliteFiles(billingDb);

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

        await using var alarm = AlarmApp.Create([], new AlarmOptions(
            DatabasePath: alarmDb,
            ResetDatabase: true,
            EventLogUrl: telemetryUrl,
            RateThreshold: 2,
            WindowSeconds: 60,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0"));
        await alarm.StartAsync();

        await using var billing = BillingApp.Create([], new BillingOptions(
            DatabasePath: billingDb,
            ResetDatabase: true,
            EventLogUrl: telemetryUrl,
            DuplicateRate: 0,
            ShuffleEnabled: false,
            ShuffleWindow: 50,
            Urls: "http://127.0.0.1:0",
            SagaTimeout: TimeSpan.FromMinutes(5)));
        await billing.StartAsync();

        using var telHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(telemetryUrl)) };
        for (var i = 0; i < 3; i++)
        {
            var response = await telHttp.PostAsJsonAsync("/ingest/squeak", new { duckId = "duck-1" });
            response.EnsureSuccessStatusCode();
        }

        using var alarmHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(alarm.Urls.First())) };
        await WaitUntilAsync(async () =>
        {
            var stats = await alarmHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("alarmCount").GetInt32() >= 1;
        });

        using var billingHttp = new HttpClient { BaseAddress = new Uri(EnsureSlash(billing.Urls.First())) };
        await WaitUntilAsync(async () =>
        {
            var stats = await billingHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("reserved").GetInt32() >= 1;
        });

        var resolve = await alarmHttp.PostAsync("/alarms/duck-1/resolve", content: null);
        resolve.EnsureSuccessStatusCode();

        await WaitUntilAsync(async () =>
        {
            var stats = await billingHttp.GetFromJsonAsync<JsonElement>("/stats");
            return stats.GetProperty("released").GetInt32() >= 1;
        });

        await billing.StopAsync();
        await alarm.StopAsync();
        await telemetry.StopAsync();
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
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
