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

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Hermes:BaseUrl is required.");

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Hermes:ApiKey is required.");

        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds))
        };

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<string> ExecuteAsync(
        string instruction,
        string? contextJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            throw new ArgumentException("Hermes instruction is required.", nameof(instruction));

        var prompt = BuildPrompt(instruction, contextJson);

        using var response = await _http.PostAsJsonAsync(
            _options.ExecutePath.TrimStart('/'),
            new
            {
                model = string.IsNullOrWhiteSpace(_options.Model) ? "hermes-agent" : _options.Model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                },
                stream = false
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Hermes returned HTTP {(int)response.StatusCode}: {body}");

        return ExtractAssistantContent(body) ?? body;
    }

    private static string BuildPrompt(string instruction, string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return instruction.Trim();

        try
        {
            using var document = JsonDocument.Parse(contextJson);
            var normalizedContext = JsonSerializer.Serialize(document.RootElement);

            return $"""
{instruction.Trim()}

Context JSON:
{normalizedContext}
""";
        }
        catch (JsonException)
        {
            return $"""
{instruction.Trim()}

Context:
{contextJson.Trim()}
""";
        }
    }

    private static string? ExtractAssistantContent(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                return null;

            var first = choices[0];

            if (!first.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
                return null;

            return content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : content.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
