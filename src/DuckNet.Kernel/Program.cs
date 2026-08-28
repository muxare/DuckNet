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

if (options.ListDlq)
{
    Environment.ExitCode = KernelDlqCli.List(options.DatabasePath, Console.Out);
    return;
}

if (options.ReplayDlqId is { } replayId)
{
    Environment.ExitCode = KernelDlqCli.Replay(
        options.DatabasePath,
        replayId,
        options.FixDlq,
        Console.Out);
    return;
}

if (options.SkipDlqId is { } skipId)
{
    Environment.ExitCode = KernelDlqCli.Skip(options.DatabasePath, skipId, Console.Out);
    return;
}

Console.WriteLine(
    $"DuckNet Step 7 — durable log + retry/DLQ for {options.Seconds}s (Ctrl+C to stop early)");
Console.WriteLine(
    $"db={options.DatabasePath} reset={options.ResetDatabase} duplicateRate={options.DuplicateRate.ToString("0.00", CultureInfo.InvariantCulture)} shuffle={shuffleState} inbox={inboxState} sequencer={sequencerState} injectPoison={options.InjectPoison}");
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
        databasePath: options.DatabasePath,
        resetDatabase: options.ResetDatabase,
        injectPoison: options.InjectPoison,
        cancellationToken: cts.Token);

    Console.WriteLine($"Published:   {result.PublishedCount} (session)");
    Console.WriteLine($"Duplicates:  {result.DuplicateDeliveries}");
    Console.WriteLine($"Counted:     {result.TotalCount} (lifetime)");
    Console.WriteLine($"Log rows:    {result.LogCount}");
    Console.WriteLine($"Log offset:  {result.LastOffset}");
    Console.WriteLine($"DLQ rows:    {result.DeadLetteredCount}");
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
    public const string DefaultDatabasePath = "ducknet-kernel.db";

    public static DemoCliOptions Parse(string[] args)
    {
        var seconds = 30;
        var duplicateRate = ParseRate(Environment.GetEnvironmentVariable("DUPLICATE_RATE"), 0.15);
        var inboxEnabled = !IsFalse(Environment.GetEnvironmentVariable("INBOX_ENABLED"));
        var shuffleEnabled = !IsFalse(Environment.GetEnvironmentVariable("SHUFFLE_ENABLED"));
        var shuffleWindow = ParseWindow(Environment.GetEnvironmentVariable("SHUFFLE_WINDOW"), 50);
        var sequencerEnabled = !IsFalse(Environment.GetEnvironmentVariable("SEQUENCER_ENABLED"));
        var databasePath = Environment.GetEnvironmentVariable("DUCKNET_DB") ?? DefaultDatabasePath;
        var resetDatabase = false;
        var injectPoison = IsTrue(Environment.GetEnvironmentVariable("INJECT_POISON_EVENT"));
        var listDlq = false;
        long? replayDlqId = null;
        long? skipDlqId = null;
        var fixDlq = false;

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

            if (arg is "--reset-db")
            {
                resetDatabase = true;
                continue;
            }

            if (arg == "--db" && i + 1 < args.Length)
            {
                databasePath = args[++i];
                continue;
            }

            if (arg.StartsWith("--db=", StringComparison.Ordinal))
            {
                databasePath = arg["--db=".Length..];
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

            if (arg is "--inject-poison")
            {
                injectPoison = true;
                continue;
            }

            if (arg is "--list-dlq")
            {
                listDlq = true;
                continue;
            }

            if (arg is "--fix" or "--fix-dlq")
            {
                fixDlq = true;
                continue;
            }

            if (arg == "--replay-dlq" && i + 1 < args.Length && long.TryParse(args[i + 1], out var replayId))
            {
                replayDlqId = replayId;
                i++;
                continue;
            }

            if (arg.StartsWith("--replay-dlq=", StringComparison.Ordinal)
                && long.TryParse(arg["--replay-dlq=".Length..], out var replayEqualsId))
            {
                replayDlqId = replayEqualsId;
                continue;
            }

            if (arg == "--skip-dlq" && i + 1 < args.Length && long.TryParse(args[i + 1], out var skipId))
            {
                skipDlqId = skipId;
                i++;
                continue;
            }

            if (arg.StartsWith("--skip-dlq=", StringComparison.Ordinal)
                && long.TryParse(arg["--skip-dlq=".Length..], out var skipEqualsId))
            {
                skipDlqId = skipEqualsId;
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
            sequencerEnabled,
            databasePath,
            resetDatabase,
            injectPoison,
            listDlq,
            replayDlqId,
            skipDlqId,
            fixDlq);
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

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || value == "1";

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
    bool SequencerEnabled,
    string DatabasePath,
    bool ResetDatabase,
    bool InjectPoison,
    bool ListDlq,
    long? ReplayDlqId,
    long? SkipDlqId,
    bool FixDlq);
