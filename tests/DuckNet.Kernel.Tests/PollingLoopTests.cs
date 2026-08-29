using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class PollingLoopTests
{
    [Fact]
    public async Task RunAsync_polls_repeatedly_until_cancelled()
    {
        // Task.Delay observes an already-cancelled token immediately, so RunAsync
        // surfaces OperationCanceledException on the poll where cancellation lands —
        // matching the four existing feeder/dispatcher RunAsync loops.
        var calls = 0;
        using var cts = new CancellationTokenSource();
        var loop = new PollingLoop(
            batchStep: _ =>
            {
                calls++;
                if (calls >= 3)
                {
                    cts.Cancel();
                }

                return Task.FromResult(0);
            },
            delay: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunAsync(cts.Token));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RunAsync_waits_the_configured_delay_between_polls()
    {
        var calls = 0;
        using var cts = new CancellationTokenSource();
        var loop = new PollingLoop(
            batchStep: _ =>
            {
                calls++;
                return Task.FromResult(0);
            },
            delay: TimeSpan.FromMilliseconds(25));

        cts.CancelAfter(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunAsync(cts.Token));

        // With a 25ms delay and a 60ms window, expect roughly 2-3 polls: enough to
        // prove the delay is applied between polls without busy-looping.
        Assert.InRange(calls, 2, 4);
    }

    [Fact]
    public async Task DrainAsync_calls_batch_step_until_it_reports_zero()
    {
        var remaining = new Queue<int>(new[] { 5, 3, 0 });
        var calls = 0;
        var loop = new PollingLoop(
            batchStep: _ =>
            {
                calls++;
                return Task.FromResult(remaining.Dequeue());
            },
            delay: TimeSpan.FromMinutes(1));

        await loop.DrainAsync();

        Assert.Equal(3, calls);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task CatchUpAsync_behaves_like_DrainAsync()
    {
        var remaining = new Queue<int>(new[] { 2, 0 });
        var loop = new PollingLoop(
            batchStep: _ => Task.FromResult(remaining.Dequeue()),
            delay: TimeSpan.FromMinutes(1));

        await loop.CatchUpAsync();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task RunAsync_swallows_exceptions_matching_the_transient_predicate()
    {
        var calls = 0;
        using var cts = new CancellationTokenSource();
        var loop = new PollingLoop(
            batchStep: _ =>
            {
                calls++;
                if (calls >= 3)
                {
                    cts.Cancel();
                    return Task.FromResult(0);
                }

                throw new InvalidOperationException("transient");
            },
            delay: TimeSpan.Zero,
            isTransient: ex => ex is InvalidOperationException);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunAsync(cts.Token));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RunAsync_rethrows_exceptions_not_matching_the_transient_predicate()
    {
        var loop = new PollingLoop(
            batchStep: _ => throw new InvalidOperationException("fatal"),
            delay: TimeSpan.Zero,
            isTransient: ex => ex is TimeoutException);

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_rethrows_when_no_transient_predicate_is_supplied()
    {
        var loop = new PollingLoop(
            batchStep: _ => throw new InvalidOperationException("fatal"),
            delay: TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_propagates_cancellation_even_when_a_transient_predicate_is_supplied()
    {
        using var cts = new CancellationTokenSource();
        var loop = new PollingLoop(
            batchStep: _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            delay: TimeSpan.Zero,
            isTransient: _ => true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => loop.RunAsync(cts.Token));
    }
}
