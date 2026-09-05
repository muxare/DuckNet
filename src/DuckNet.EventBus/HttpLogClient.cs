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
        CancellationToken cancellationToken = default,
        int? partition = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var url = $"bus/events?after={offset}&limit={limit}";
        if (partition is not null)
        {
            url += $"&partition={partition.Value}";
        }

        using var response = await _http
            .GetAsync(url, cancellationToken)
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

    public async Task AppendBatchAsync(IReadOnlyList<EventEnvelope> envelopes, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(envelopes, EnvelopeJson.Options),
            Encoding.UTF8,
            "application/json");
        using var response = await _http
            .PostAsync("bus/events/batch", content, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
