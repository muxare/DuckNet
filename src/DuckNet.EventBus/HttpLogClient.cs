using System.Text;
using System.Text.Json;
using DuckNet.Contracts;

namespace DuckNet.EventBus;

/// <summary>
/// Cross-process log access. Telemetry owns the SQLite write path; other Centers
/// talk to this HTTP adapter and never open Telemetry's database file.
/// </summary>
public sealed class HttpLogClient
{
    private readonly HttpClient _http;

    public HttpLogClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<EventEnvelope>> ReadAfterAsync(
        long offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var response = await _http
            .GetAsync($"bus/events?after={offset}&limit={limit}", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var rows = await JsonSerializer
            .DeserializeAsync<List<EventEnvelope>>(stream, EnvelopeJson.Options, cancellationToken)
            .ConfigureAwait(false);
        return rows ?? [];
    }

    public async Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            EnvelopeJson.Serialize(envelope),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("bus/events", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
