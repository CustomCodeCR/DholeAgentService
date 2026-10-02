using System.Text;
using System.Text.Json;
using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Network;

public sealed class MaerskOfferInterceptor
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<CapturedMaerskOfferResponse> WaitForOfferAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Task? collectionComplete = null)
    {
        var sync = new object();
        var captures = new List<CapturedMaerskOfferResponse>();
        var inFlight = 0;
        var lastActivityUtc = DateTime.MinValue;

        EventHandler<IResponse>? handler = null;
        handler = (_, response) =>
        {
            if (!IsDeparturesOffersResponse(response))
                return;

            lock (sync)
            {
                inFlight++;
                lastActivityUtc = DateTime.UtcNow;
            }

            _ = CaptureResponseAsync(response);
        };

        async Task CaptureResponseAsync(IResponse response)
        {
            try
            {
                var headers = await response.AllHeadersAsync();
                headers.TryGetValue("correlation-id", out var correlationId);

                if (string.IsNullOrWhiteSpace(correlationId))
                    headers.TryGetValue("x-correlation-id", out correlationId);

                var json = await response.TextAsync();

                if (string.IsNullOrWhiteSpace(json))
                    return;

                lock (sync)
                {
                    captures.Add(new CapturedMaerskOfferResponse(
                        response.Request.PostData,
                        response.Status,
                        correlationId,
                        json));

                    lastActivityUtc = DateTime.UtcNow;
                }
            }
            catch
            {
                // A single browser response can disappear while Maersk replaces
                // the page state. Keep listening for the remaining batches.
            }
            finally
            {
                lock (sync)
                {
                    inFlight = Math.Max(0, inFlight - 1);
                    lastActivityUtc = DateTime.UtcNow;
                }
            }
        }

        page.Response += handler;

        var startedAtUtc = DateTime.UtcNow;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<CapturedMaerskOfferResponse> snapshot;
                int pending;
                DateTime lastActivity;

                lock (sync)
                {
                    snapshot = captures.ToList();
                    pending = inFlight;
                    lastActivity = lastActivityUtc;
                }

                var elapsed = DateTime.UtcNow - startedAtUtc;

                var traversalComplete =
                    collectionComplete is null || collectionComplete.IsCompleted;

                if (snapshot.Count > 0
                    && pending == 0
                    && traversalComplete
                    && DateTime.UtcNow - lastActivity >= QuietPeriod)
                {
                    return MergeCaptures(snapshot);
                }

                if (elapsed >= timeout)
                {
                    if (snapshot.Count > 0)
                        return MergeCaptures(snapshot);

                    throw new TimeoutException(
                        "Maersk departures/offers response was not captured.");
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            page.Response -= handler;
        }
    }

    private static bool IsDeparturesOffersResponse(IResponse response)
        => response.Url.Contains(
               "/v2/departures/offers",
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               response.Request.Method,
               "POST",
               StringComparison.OrdinalIgnoreCase);

    private static CapturedMaerskOfferResponse MergeCaptures(
        IReadOnlyCollection<CapturedMaerskOfferResponse> captures)
    {
        var distinct = captures
            .DistinctBy(
                x => $"{x.CorrelationId}|{x.ResponseJson}",
                StringComparer.Ordinal)
            .ToArray();

        var mergedJson = MergeJsonBodies(
            distinct.Select(x => x.ResponseJson));

        var correlationIds = distinct
            .Select(x => x.CorrelationId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CapturedMaerskOfferResponse(
            distinct
                .Select(x => x.RequestBody)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            distinct.Last().Status,
            correlationIds.Length == 0
                ? null
                : string.Join(",", correlationIds),
            mergedJson,
            distinct.Length);
    }

    private static string MergeJsonBodies(IEnumerable<string> bodies)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartArray();

        foreach (var body in bodies)
        {
            try
            {
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                        item.WriteTo(writer);
                }
                else
                {
                    document.RootElement.WriteTo(writer);
                }
            }
            catch (JsonException)
            {
                // Ignore a malformed batch but keep valid Maersk batches.
            }
        }

        writer.WriteEndArray();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
