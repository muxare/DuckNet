using DuckNet.Kernel.Consumer;

namespace DuckNet.Kernel.Tests;

public class RetryPipelineTests
{
    [Fact]
    public void Succeeds_on_first_attempt()
    {
        var pipeline = new RetryPipeline(maxAttempts: 5, baseDelay: TimeSpan.Zero);
        var calls = 0;

        var result = pipeline.Execute(() => calls++);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, calls);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Retries_then_succeeds()
    {
        var pipeline = new RetryPipeline(maxAttempts: 5, baseDelay: TimeSpan.Zero);
        var calls = 0;

        var result = pipeline.Execute(() =>
        {
            calls++;
            if (calls < 3)
            {
                throw new InvalidOperationException("transient");
            }
        });

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Exhausts_attempts_and_returns_last_error()
    {
        var pipeline = new RetryPipeline(maxAttempts: 5, baseDelay: TimeSpan.Zero);
        var calls = 0;

        var result = pipeline.Execute(() =>
        {
            calls++;
            throw new InvalidOperationException("poison");
        });

        Assert.False(result.Succeeded);
        Assert.Equal(5, result.Attempts);
        Assert.Equal(5, calls);
        Assert.Equal("poison", result.Error!.Message);
    }

    [Fact]
    public void Backoff_doubles_between_attempts()
    {
        var delays = new List<TimeSpan>();
        var pipeline = new RetryPipeline(
            maxAttempts: 4,
            baseDelay: TimeSpan.FromMilliseconds(10),
            sleep: delays.Add);

        pipeline.Execute(() => throw new InvalidOperationException("always"));

        Assert.Equal(
            new[] { 10.0, 20.0, 40.0 },
            delays.Select(d => d.TotalMilliseconds));
    }
}
