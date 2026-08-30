using DuckNet.AlarmCenter;
using DuckNet.Kernel.Persistence;
using DuckNet.TelemetryCenter;

namespace DuckNet.AlarmCenter.Tests;

public class CenterIsolationTests
{
    [Fact]
    public void Centers_do_not_reference_each_other()
    {
        var root = RepoRoot();
        var alarmCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.AlarmCenter", "DuckNet.AlarmCenter.csproj"));
        var telemetryCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.TelemetryCenter", "DuckNet.TelemetryCenter.csproj"));

        Assert.DoesNotContain("DuckNet.TelemetryCenter", alarmCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.AlarmCenter", telemetryCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.DashboardCenter", alarmCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.DashboardCenter", telemetryCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", alarmCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", telemetryCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void AlarmCenter_source_never_opens_event_log_store()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(
            Path.Combine(root, "src", "DuckNet.AlarmCenter"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("EventLogStore", text, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.TelemetryCenter", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Separate_schemas_do_not_share_tables()
    {
        using var telemetry = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        using var alarm = KernelDb.OpenInMemory(CenterSchema.Alarm);

        var telemetryTables = telemetry.TableNames();
        var alarmTables = alarm.TableNames();

        Assert.Contains("event_log", telemetryTables);
        Assert.Contains("duck_state", telemetryTables);
        Assert.DoesNotContain("alarms", telemetryTables);

        Assert.Contains("alarms", alarmTables);
        Assert.Contains("squeak_window", alarmTables);
        Assert.DoesNotContain("event_log", alarmTables);
        Assert.DoesNotContain("duck_state", alarmTables);
        Assert.DoesNotContain("billing_sagas", alarmTables);
        Assert.DoesNotContain("billing_sagas", telemetryTables);
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
