using DuckNet.Kernel;

var options = DemoCli.Parse(args);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var inboxState = options.InboxEnabled ? "on" : "OFF (mis-demo — counts will drift)";
Console.WriteLine(
    $"DuckNet Step 1 — at-least-once + inbox for {options.Seconds}s (Ctrl+C to stop early)");
Console.WriteLine(
    $"duplicateRate={options.DuplicateRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} inbox={inboxState}");

try
{
    var result = await KernelRunner.RunDemoAsync(
        TimeSpan.FromSeconds(options.Seconds),
        duckCount: 5,
        seed: 42,
        duplicateRate: options.DuplicateRate,
        inboxEnabled: options.InboxEnabled,
        logEvery: 50,
        logDuplicates: true,
        output: Console.Out,
        duplicateMaxDelay: TimeSpan.FromMilliseconds(40),
        cancellationToken: cts.Token);

    Console.WriteLine($"Published:  {result.PublishedCount}");
    Console.WriteLine($"Duplicates: {result.DuplicateDeliveries}");
    Console.WriteLine($"Counted:    {result.TotalCount}");
    Console.WriteLine($"Skipped:    {result.DuplicateSkips}");
    foreach (var (duckId, count) in result.CountsByDuck.OrderBy(x => x.Key))
    {
        Console.WriteLine($"  {duckId}: {count}");
    }

    if (!options.InboxEnabled && result.DuplicateDeliveries > 0)
    {
        Console.WriteLine("Inbox disabled: counted total includes duplicate deliveries.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped.");
}

internal static class DemoCli
{
    public static DemoCliOptions Parse(string[] args)
    {
        var seconds = 30;
        var duplicateRate = ParseRate(Environment.GetEnvironmentVariable("DUPLICATE_RATE"), 0.15);
        var inboxEnabled = !IsFalse(Environment.GetEnvironmentVariable("INBOX_ENABLED"));

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out seconds))
            {
                i++;
                continue;
            }

            if (arg.StartsWith("--seconds=", StringComparison.Ordinal)
                && int.TryParse(arg["--seconds=".Length..], out seconds))
            {
                continue;
            }

            if (arg is "--disable-inbox" or "--mis-demo")
            {
                inboxEnabled = false;
                continue;
            }

            if (arg == "--duplicate-rate" && i + 1 < args.Length)
            {
                duplicateRate = ParseRate(args[++i], duplicateRate);
                continue;
            }

            if (arg.StartsWith("--duplicate-rate=", StringComparison.Ordinal))
            {
                duplicateRate = ParseRate(arg["--duplicate-rate=".Length..], duplicateRate);
            }
        }

        return new DemoCliOptions(seconds, duplicateRate, inboxEnabled);
    }

    private static double ParseRate(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= 0 and <= 1
            ? parsed
            : fallback;

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
        || value == "0";
}

internal sealed record DemoCliOptions(int Seconds, double DuplicateRate, bool InboxEnabled);
