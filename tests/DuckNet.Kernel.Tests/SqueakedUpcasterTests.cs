using System.Text.Json;
using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.Kernel.Tests;

public class SqueakedUpcasterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-28T10:00:00Z");

    [Fact]
    public void V1_upcasts_to_v2_with_volume_default_zero()
    {
        var source = SqueakedEnvelope.CreateV1(new SqueakedV1("duck-1", 3, At));
        Assert.Equal(1, source.Version);
        Assert.DoesNotContain("volumeDb", source.PayloadJson, StringComparison.OrdinalIgnoreCase);

        var upcast = EventUpcasterPipeline.Default.Upcast(source);

        Assert.Equal(source.EventId, upcast.EventId);
        Assert.Equal(source.PartitionKey, upcast.PartitionKey);
        Assert.Equal(source.SequenceNumber, upcast.SequenceNumber);
        Assert.Equal(source.OccurredAt, upcast.OccurredAt);
        Assert.Equal(source.LogOffset, upcast.LogOffset);
        Assert.Equal(2, upcast.Version);

        var squeaked = SqueakedEnvelope.Parse(upcast);
        Assert.Equal("duck-1", squeaked.DuckId);
        Assert.Equal(3, squeaked.SequenceNumber);
        Assert.Equal(At, squeaked.OccurredAt);
        Assert.Equal(SqueakedV1ToV2Upcaster.DefaultVolumeDb, squeaked.VolumeDb);
        Assert.Equal(0, squeaked.VolumeDb);
    }

    [Fact]
    public void V2_passes_through_unchanged()
    {
        var source = SqueakedEnvelope.Create(new Squeaked("duck-2", 1, At, 72.5));
        var upcast = EventUpcasterPipeline.Default.Upcast(source);

        Assert.Equal(source.EventId, upcast.EventId);
        Assert.Equal(2, upcast.Version);
        Assert.Equal(source.PayloadJson, upcast.PayloadJson);
        Assert.Equal(72.5, SqueakedEnvelope.Parse(upcast).VolumeDb);
    }

    [Fact]
    public void Unknown_type_passes_through()
    {
        var source = AlarmRaisedEnvelope.Create(
            new AlarmRaised("duck-1", 12, At),
            sequenceNumber: 1);
        var upcast = EventUpcasterPipeline.Default.Upcast(source);
        Assert.Same(source, upcast);
        Assert.Equal(1, upcast.Version);
    }

    [Fact]
    public void Parse_rejects_v1_until_upcast()
    {
        var source = SqueakedEnvelope.CreateV1(new SqueakedV1("duck-1", 1, At));
        var ex = Assert.Throws<InvalidOperationException>(() => SqueakedEnvelope.Parse(source));
        Assert.Contains("must be upcast", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanUpcast_only_squeaked_v1()
    {
        var upcaster = new SqueakedV1ToV2Upcaster();
        Assert.True(upcaster.CanUpcast("Squeaked", 1));
        Assert.False(upcaster.CanUpcast("Squeaked", 2));
        Assert.False(upcaster.CanUpcast("AlarmRaised", 1));
    }

    [Fact]
    public void Create_emits_version_2_payload_with_volume()
    {
        var envelope = SqueakedEnvelope.Create(new Squeaked("duck-1", 1, At, 61));
        Assert.Equal(2, envelope.Version);
        using var doc = JsonDocument.Parse(envelope.PayloadJson);
        Assert.Equal(61, doc.RootElement.GetProperty("volumeDb").GetDouble());
    }
}
