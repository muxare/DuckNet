namespace DuckNet.Kernel.Consumer;

/// <summary>
/// Consumer-owned retry around a handler. Not part of the bus.
/// Exhausted attempts are the caller's cue to dead-letter and continue.
/// </summary>
public sealed class RetryPipeline
{
    public const int DefaultMaxAttempts = 5;

    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(50);

    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly Action<TimeSpan> _sleep;

    public RetryPipeline(
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? baseDelay = null,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay ?? DefaultBaseDelay;
        _sleep = sleep ?? SleepIfPositive;
    }

    public int MaxAttempts => _maxAttempts;

    private static void SleepIfPositive(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    public RetryResult Execute(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? last = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                action();
                return RetryResult.Ok(attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt < _maxAttempts)
                {
                    var factor = Math.Pow(2, attempt - 1);
                    _sleep(TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * factor));
                }
            }
        }

        return RetryResult.Fail(_maxAttempts, last!);
    }
}

public readonly record struct RetryResult(bool Succeeded, int Attempts, Exception? Error)
{
    public static RetryResult Ok(int attempts) => new(true, attempts, null);

    public static RetryResult Fail(int attempts, Exception error) => new(false, attempts, error);
}
