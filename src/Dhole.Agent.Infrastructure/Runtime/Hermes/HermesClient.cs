using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.Infrastructure.Runtime.Hermes;

public sealed class HermesClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HermesOptions _options;

    public HermesClient(IOptions<HermesOptions> options)
    {
        _options = options.Value;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds))
        };
    }

    public async Task<string> ExecuteAsync(string instruction, string? contextJson, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            _options.ExecutePath,
            new { instruction, context = ParseContext(contextJson) },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Hermes returned HTTP {(int)response.StatusCode}: {body}");

        return body;
    }

    private static object? ParseContext(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(contextJson);
        }
        catch (JsonException)
        {
            return contextJson;
        }
    }

    public void Dispose() => _http.Dispose();
}
