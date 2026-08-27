using System.Globalization;
using DuckNet.Kernel;

var options = DemoCli.Parse(args);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var inboxState = options.InboxEnabled ? "on" : "OFF";
var sequencerState = options.SequencerEnabled ? "on" : "OFF";
var shuffleState = options.ShuffleEnabled
    ? $"on (window={options.ShuffleWindow})"
    : "off";
Console.WriteLine(
    $"DuckNet Step 2 — out-of-order + per-key sequencer for {options.Seconds}s (Ctrl+C to stop early)");
Console.WriteLine(
    $"duplicateRate={options.DuplicateRate.ToString("0.00", CultureInfo.InvariantCulture)} shuffle={shuffleState} inbox={inboxState} sequencer={sequencerState}");
if (!options.InboxEnabled || !options.SequencerEnabled)
{
    Console.WriteLine("Mis-demo: consumer defenses off — counts and/or per-key order may be wrong.");
}

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
        shuffleEnabled: options.ShuffleEnabled,
        shuffleWindow: options.ShuffleWindow,
        sequencerEnabled: options.SequencerEnabled,
        cancellationToken: cts.Token);

    Console.WriteLine($"Published:   {result.PublishedCount}");
    Console.WriteLine($"Duplicates:  {result.DuplicateDeliveries}");
    Console.WriteLine($"Counted:     {result.TotalCount}");
    Console.WriteLine($"Inbox skips: {result.DuplicateSkips}");
    Console.WriteLine($"Late drops:  {result.SequencerLateDrops}");
    Console.WriteLine($"Out of order:{result.OutOfOrderCount}");
    foreach (var (duckId, count) in result.CountsByDuck.OrderBy(x => x.Key))
    {
        Console.WriteLine($"  {duckId}: {count}");
    }

    if (!options.InboxEnabled && !options.SequencerEnabled && result.DuplicateDeliveries > 0)
    {
        Console.WriteLine("Inbox and sequencer disabled: counted total includes duplicate deliveries.");
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
        var shuffleEnabled = !IsFalse(Environment.GetEnvironmentVariable("SHUFFLE_ENABLED"));
        var shuffleWindow = ParseWindow(Environment.GetEnvironmentVariable("SHUFFLE_WINDOW"), 50);
        var sequencerEnabled = !IsFalse(Environment.GetEnvironmentVariable("SEQUENCER_ENABLED"));

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

            if (arg is "--disable-inbox")
            {
                inboxEnabled = false;
                continue;
            }

            if (arg is "--mis-demo")
            {
                inboxEnabled = false;
                sequencerEnabled = false;
                continue;
            }

            if (arg is "--disable-sequencer")
            {
                sequencerEnabled = false;
                continue;
            }

            if (arg is "--no-shuffle")
            {
                shuffleEnabled = false;
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
                continue;
            }

            if (arg == "--shuffle-window" && i + 1 < args.Length)
            {
                shuffleWindow = ParseWindow(args[++i], shuffleWindow);
                continue;
            }

            if (arg.StartsWith("--shuffle-window=", StringComparison.Ordinal))
            {
                shuffleWindow = ParseWindow(arg["--shuffle-window=".Length..], shuffleWindow);
            }
        }

        return new DemoCliOptions(
            seconds,
            duplicateRate,
            inboxEnabled,
            shuffleEnabled,
            shuffleWindow,
            sequencerEnabled);
    }

    private static double ParseRate(string? value, double fallback) =>
        double.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= 0 and <= 1
            ? parsed
            : fallback;

    private static int ParseWindow(string? value, int fallback) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? parsed
            : fallback;

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
        || value == "0";
}

internal sealed record DemoCliOptions(
    int Seconds,
    double DuplicateRate,
    bool InboxEnabled,
    bool ShuffleEnabled,
    int ShuffleWindow,
    bool SequencerEnabled);
