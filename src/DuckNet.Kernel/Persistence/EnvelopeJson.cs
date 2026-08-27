using System.Text.Json;
using DuckNet.Kernel.Transport;

namespace DuckNet.Kernel.Persistence;

internal static class EnvelopeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(EventEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static EventEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<EventEnvelope>(json, Options)
        ?? throw new InvalidOperationException("Outbox payload is not an EventEnvelope.");
}
