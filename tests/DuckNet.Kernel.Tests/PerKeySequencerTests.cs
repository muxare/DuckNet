using DuckNet.Kernel.Consumer;
using DuckNet.Kernel.Domain.Events;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Tests;

public class PerKeySequencerTests
{
    [Fact]
    public void Out_of_order_across_keys_emits_in_per_key_sequence()
    {
        var sequencer = new PerKeySequencer();

        var released = new List<EventEnvelope>();
        released.AddRange(sequencer.Offer(Squeak("duck-B", 1)));
        released.AddRange(sequencer.Offer(Squeak("duck-A", 2)));
        released.AddRange(sequencer.Offer(Squeak("duck-A", 1)));

        Assert.Equal(
            new[] { ("duck-B", 1L), ("duck-A", 1L), ("duck-A", 2L) },
            released.Select(e => (e.PartitionKey, e.SequenceNumber)));
    }

    [Fact]
    public void Late_sequence_is_dropped()
    {
        var sequencer = new PerKeySequencer();
        var first = Squeak("duck-A", 1);

        Assert.Single(sequencer.Offer(first));
        Assert.Empty(sequencer.Offer(first));
        Assert.Equal(1, sequencer.LateDropCount);
    }

    [Fact]
    public void Duplicate_of_buffered_seq_does_not_emit_twice()
    {
        var sequencer = new PerKeySequencer();
        var a2 = Squeak("duck-A", 2);

        Assert.Empty(sequencer.Offer(a2));
        Assert.Empty(sequencer.Offer(a2));

        var released = sequencer.Offer(Squeak("duck-A", 1));
        Assert.Equal(new[] { 1L, 2L }, released.Select(e => e.SequenceNumber));
        Assert.Equal(1, sequencer.BufferedOverwriteCount);
    }

    [Fact]
    public void Gap_is_logged_after_timeout_without_inventing_events()
    {
        var sequencer = new PerKeySequencer();
        var log = new StringWriter();
        var t0 = DateTimeOffset.Parse("2026-08-27T10:00:00Z");

        Assert.Empty(sequencer.Offer(Squeak("duck-A", 2), t0));

        sequencer.ReportGaps(TimeSpan.FromSeconds(5), log, t0.AddSeconds(4));
        Assert.Empty(log.ToString());
        Assert.Equal(0, sequencer.GapReportCount);
        Assert.Equal(1, sequencer.BufferedCount);

        sequencer.ReportGaps(TimeSpan.FromSeconds(5), log, t0.AddSeconds(5));
        Assert.Contains("Gap on duck-A: waiting for seq 1", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("buffered [2]", log.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, sequencer.GapReportCount);
        Assert.Equal(1, sequencer.BufferedCount);

        sequencer.ReportGaps(TimeSpan.FromSeconds(5), log, t0.AddSeconds(10));
        Assert.Equal(1, sequencer.GapReportCount);
    }

    [Fact]
    public void Seeded_next_expected_emits_from_last_handled_plus_one()
    {
        var sequencer = new PerKeySequencer(new Dictionary<string, long> { ["duck-A"] = 3 });

        Assert.Empty(sequencer.Offer(Squeak("duck-A", 3)));
        Assert.Equal(1, sequencer.LateDropCount);

        var released = sequencer.Offer(Squeak("duck-A", 4));
        Assert.Equal(4, Assert.Single(released).SequenceNumber);
    }

    private static EventEnvelope Squeak(string duckId, long seq) =>
        SqueakedEnvelope.Create(new Squeaked(duckId, seq, DateTimeOffset.UtcNow));
}
