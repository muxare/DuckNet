using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class ShufflerMiddlewareTests
{
    [Fact]
    public async Task Full_window_is_released_as_a_permutation_not_fifo()
    {
        var inner = new RecordingBus();
        var shuffler = new ShufflerMiddleware(inner, windowSize: 5, seed: 1);

        for (var seq = 1; seq <= 5; seq++)
        {
            await shuffler.PublishAsync(Squeak("duck-1", seq));
        }

        Assert.Equal(5, inner.Published.Count);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, inner.Published.Select(e => e.SequenceNumber).OrderBy(s => s));
        Assert.NotEqual(new[] { 1L, 2L, 3L, 4L, 5L }, inner.Published.Select(e => e.SequenceNumber));
        Assert.Equal(1, shuffler.FlushCount);
    }

    [Fact]
    public async Task Disabled_or_window_one_is_passthrough()
    {
        var inner = new RecordingBus();
        var shuffler = new ShufflerMiddleware(inner, windowSize: 50, seed: 1, enabled: false);

        await shuffler.PublishAsync(Squeak("duck-1", 1));
        await shuffler.PublishAsync(Squeak("duck-1", 2));

        Assert.Equal(new[] { 1L, 2L }, inner.Published.Select(e => e.SequenceNumber));
        Assert.Equal(0, shuffler.FlushCount);
    }

    [Fact]
    public async Task FlushAsync_releases_the_remainder_shuffled()
    {
        var inner = new RecordingBus();
        var shuffler = new ShufflerMiddleware(inner, windowSize: 50, seed: 3);

        for (var seq = 1; seq <= 4; seq++)
        {
            await shuffler.PublishAsync(Squeak("duck-1", seq));
        }

        Assert.Empty(inner.Published);

        await shuffler.FlushAsync();

        Assert.Equal(4, inner.Published.Count);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, inner.Published.Select(e => e.SequenceNumber).OrderBy(s => s));
        Assert.NotEqual(new[] { 1L, 2L, 3L, 4L }, inner.Published.Select(e => e.SequenceNumber));
        Assert.Equal(1, shuffler.FlushCount);
    }

    private static EventEnvelope Squeak(string duckId, long seq) =>
        SqueakedEnvelope.Create(new Squeaked(duckId, seq, DateTimeOffset.UtcNow));

    private sealed class RecordingBus : IEventBus
    {
        public List<EventEnvelope> Published { get; } = [];

        public ValueTask PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<EventEnvelope> SubscribeAsync(
            string consumerGroup,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
