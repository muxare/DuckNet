namespace DuckNet.EventBus.Tests;

public class CenterIsolationFromBrokerTests
{
    [Theory]
    [InlineData("DuckNet.AlarmCenter")]
    [InlineData("DuckNet.BillingCenter")]
    [InlineData("DuckNet.DashboardCenter")]
    [InlineData("DuckNet.TelemetryCenter")]
    public void Center_csproj_does_not_reference_rabbitmq(string center)
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", center, $"{center}.csproj"));
        Assert.DoesNotContain("RabbitMQ", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Testcontainers", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Center_handler_files_do_not_reference_rabbitmq()
    {
        var root = Path.Combine(RepoRoot(), "src");
        var handlers = Directory.GetFiles(root, "*Consumer.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "*Store.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(root, "*ReadModel.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(root, "*Worker.cs", SearchOption.AllDirectories))
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}DuckNet.", StringComparison.Ordinal)
                && path.Contains("Center", StringComparison.Ordinal));

        foreach (var file in handlers)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("RabbitMQ", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RabbitMq", text, StringComparison.Ordinal);
        }
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
