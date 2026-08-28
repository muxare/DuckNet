using DuckNet.Kernel.Consumer;

namespace DuckNet.Kernel.Tests;

internal static class ConsumerWait
{
    public static Task UntilCountAsync(
        SqueakCounter counter,
        long expected,
        CancellationToken cancellationToken) =>
        UntilAsync(() => counter.TotalCount >= expected, cancellationToken);

    public static Task UntilAttemptsAsync(
        SqueakCounter counter,
        long expected,
        CancellationToken cancellationToken) =>
        UntilAsync(() => counter.AttemptCount >= expected, cancellationToken);

    public static Task UntilDeadLettersAsync(
        SqueakCounter counter,
        long expected,
        CancellationToken cancellationToken) =>
        UntilAsync(() => counter.DeadLetteredCount >= expected, cancellationToken);

    private static async Task UntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!done())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
