using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.Kernel.Tests;

public class OrePartUpcasterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-04T10:00:00Z");

    [Fact]
    public void SensorReading_v1_upcasts_tenant_default()
    {
        var source = SensorReadingReportedEnvelope.CreateV1(
            new SensorReadingReportedV1("TRK-001", 1, At, 4000, 2, 70));
        var upcast = EventUpcasterPipeline.Default.Upcast(source);
        Assert.Equal(source.EventId, upcast.EventId);
        Assert.Equal(2, upcast.Version);
        Assert.Equal(OrePartTenants.Default, SensorReadingReportedEnvelope.Parse(upcast).TenantId);
        Assert.Throws<InvalidOperationException>(() => SensorReadingReportedEnvelope.Parse(source));
    }

    [Fact]
    public void AssetHealthPredicted_v1_upcasts_health_v1()
    {
        var source = AssetHealthPredictedEnvelope.CreateV1(
            new AssetHealthPredictedV1("TRK-001", 0.4, At, "Inspect", 2, 70, 4000),
            sequenceNumber: 1);
        var upcast = EventUpcasterPipeline.Default.Upcast(source);
        Assert.Equal(source.EventId, upcast.EventId);
        Assert.Equal(HealthModels.V1, AssetHealthPredictedEnvelope.Parse(upcast).ModelVersion);
        Assert.Throws<InvalidOperationException>(() => AssetHealthPredictedEnvelope.Parse(source));
    }
}
