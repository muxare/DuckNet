using DuckNet.Contracts;
using DuckNet.EventBus;

namespace DuckNet.EventBus.Tests;

internal static class TestEnvelope
{
    public static EventEnvelope Squeak(
        string duckId = "duck-1",
        long seq = 1,
        Guid? eventId = null,
        string? traceId = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        string? causationId = "parent-1",
        long logOffset = 7)
    {
        var envelope = SqueakedEnvelope.Create(
            new Squeaked(duckId, seq, DateTimeOffset.Parse("2026-08-30T12:00:00Z")),
            eventId,
            traceId,
            causationId);
        return envelope with { LogOffset = logOffset };
    }
}
