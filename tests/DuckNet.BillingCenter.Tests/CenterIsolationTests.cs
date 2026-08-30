using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter.Tests;

public class CenterIsolationTests
{
    [Fact]
    public void Centers_do_not_reference_each_other()
    {
        var root = RepoRoot();
        var billingCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.BillingCenter", "DuckNet.BillingCenter.csproj"));
        var alarmCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.AlarmCenter", "DuckNet.AlarmCenter.csproj"));
        var telemetryCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.TelemetryCenter", "DuckNet.TelemetryCenter.csproj"));
        var dashboardCsproj = File.ReadAllText(
            Path.Combine(root, "src", "DuckNet.DashboardCenter", "DuckNet.DashboardCenter.csproj"));

        Assert.DoesNotContain("DuckNet.AlarmCenter", billingCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.TelemetryCenter", billingCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.DashboardCenter", billingCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", alarmCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", telemetryCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("DuckNet.BillingCenter", dashboardCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void BillingCenter_source_never_opens_other_center_stores()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(
            Path.Combine(root, "src", "DuckNet.BillingCenter"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("EventLogStore", text, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("alarm.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dashboard.db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.TelemetryCenter", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.AlarmCenter", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DuckNet.DashboardCenter", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Separate_schemas_do_not_share_tables()
    {
        using var telemetry = KernelDb.OpenInMemory(CenterSchema.Telemetry);
        using var alarm = KernelDb.OpenInMemory(CenterSchema.Alarm);
        using var billing = KernelDb.OpenInMemory(CenterSchema.Billing);

        Assert.Contains("billing_sagas", billing.TableNames());
        Assert.Contains("outbox", billing.TableNames());
        Assert.Contains("inbox", billing.TableNames());
        Assert.DoesNotContain("event_log", billing.TableNames());
        Assert.DoesNotContain("alarms", billing.TableNames());
        Assert.DoesNotContain("squeaks_by_duck_hour", billing.TableNames());

        Assert.DoesNotContain("billing_sagas", telemetry.TableNames());
        Assert.DoesNotContain("billing_sagas", alarm.TableNames());
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
