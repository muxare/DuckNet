using DuckNet.Kernel.Persistence;

namespace DuckNet.DashboardCenter.Tests;

public class CenterIsolationTests
{
    [Fact]
    public void Centers_do_not_reference_each_other()
    {
        var root = RepoRoot();
        var dashboardCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.DashboardCenter", "DuckNet.DashboardCenter.csproj"));
        var alarmCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.AlarmCenter", "DuckNet.AlarmCenter.csproj"));
        var telemetryCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.TelemetryCenter", "DuckNet.TelemetryCenter.csproj"));

        Assert.DoesNotContain("DuckNet.TelemetryCenter", dashboardCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.AlarmCenter", dashboardCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.DashboardCenter", alarmCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.DashboardCenter", telemetryCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", dashboardCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardCenter_source_never_opens_other_center_stores()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(
            Path.Combine(root, "src", "DuckNet.DashboardCenter"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("EventLogStore", text, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("alarm.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.TelemetryCenter", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.AlarmCenter", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.BillingCenter", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dashboard_schema_is_a_read_model_not_a_log()
    {
        using var telemetry = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        using var alarm = KernelDb.OpenInMemory(CenterSchema.Alarm);
        using var dashboard = KernelDb.OpenInMemory(CenterSchema.Dashboard);

        var dashboardTables = dashboard.TableNames();
        Assert.Contains("squeaks_by_duck_hour", dashboardTables);
        Assert.Contains("inbox", dashboardTables);
        Assert.Contains("consumer_offsets", dashboardTables);
        Assert.DoesNotContain("event_log", dashboardTables);
        Assert.DoesNotContain("outbox", dashboardTables);
        Assert.DoesNotContain("alarms", dashboardTables);
        Assert.DoesNotContain("duck_state", dashboardTables);

        Assert.DoesNotContain("billing_sagas", telemetry.TableNames());
        Assert.DoesNotContain("billing_sagas", alarm.TableNames());
        Assert.DoesNotContain("squeaks_by_duck_hour", telemetry.TableNames());
        Assert.DoesNotContain("squeaks_by_duck_hour", alarm.TableNames());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DuckNet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("DuckNet.slnx not found.");
    }
}
