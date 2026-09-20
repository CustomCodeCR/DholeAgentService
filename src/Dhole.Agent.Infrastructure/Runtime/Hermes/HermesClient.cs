using System.Net.Http.Headers;
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

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<string> ExecuteAsync(
        string instruction,
        string? contextJson,
        CancellationToken cancellationToken)
    {
        var context = NormalizeContext(contextJson);
        var instructions = context is null
            ? "Execute the requested task using the available Hermes tools."
            : $"Execute the requested task using the available Hermes tools. Context: {context}";

        using var response = await _http.PostAsJsonAsync(
            _options.ExecutePath,
            new
            {
                model = _options.ModelName,
                input = instruction,
                instructions,
                store = false
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Hermes returned HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    private static string? NormalizeContext(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(contextJson);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return contextJson;
        }
    }

    public void Dispose() => _http.Dispose();
}
