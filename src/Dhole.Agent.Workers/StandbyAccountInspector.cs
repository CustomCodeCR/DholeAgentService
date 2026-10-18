using System.Text.Json;
using Microsoft.Playwright;

namespace Dhole.Agent.Workers;

/// <summary>
/// Confirms a rendered Maersk Hub dashboard in the already running isolated
/// standby browser. Does not retrieve account names, usernames, customer IDs,
/// cookies or tokens. Never starts a Worker or navigates the browser.
/// </summary>
internal static class StandbyAccountInspector
{
    private sealed record HubProof(bool HubAuthenticated);

    public static async Task<int> RunAsync()
    {
        var reason = "hub_dashboard_not_visible";
        try
        {
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync(
                "http://127.0.0.1:9222",
                new BrowserTypeConnectOverCDPOptions { Timeout = 8_000 });
            foreach (var context in browser.Contexts)
            foreach (var page in context.Pages)
            {
                if (!OfficialHubPage(page.Url))
                    continue;
                reason = "hub_dashboard_not_authenticated";
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (await page.EvaluateAsync<bool>(JavaScriptHubEvidence))
                    {
                        PrintProof();
                        return 0;
                    }
                    await Task.Delay(400);
                }
            }
        }
        catch (PlaywrightException)
        {
            reason = "cdp_or_page_inspection_failed";
        }
        catch (TimeoutException)
        {
            reason = "browser_inspection_timed_out";
        }
        catch (Exception)
        {
            reason = "unexpected_inspection_error";
        }

        // An existing Chrome tab can appear through /json/list while
        // Playwright's connected browser contexts do not enumerate it.
        try
        {
            var result = await InspectDirectPageTargetAsync();
            if (result.Confirmed)
            {
                PrintProof();
                return 0;
            }
            reason = result.Reason;
        }
        catch (Exception)
        {
            reason = "cdp_direct_target_inspection_failed";
        }

        Console.Error.WriteLine("STANDBY_MAERSK_HUB_NOT_VERIFIED reason=" + reason);
        return 3;
    }

    private static void PrintProof() =>
        Console.WriteLine(JsonSerializer.Serialize(new HubProof(true)));

    private static async Task<(bool Confirmed, string Reason)> InspectDirectPageTargetAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var targetJson = await http.GetStringAsync("http://127.0.0.1:9222/json/list");
        using var targets = JsonDocument.Parse(targetJson);
        if (targets.RootElement.ValueKind != JsonValueKind.Array)
            return (false, "cdp_targets_invalid");

        var pages = 0;
        var hubs = 0;
        var unreachable = 0;
        foreach (var target in targets.RootElement.EnumerateArray())
        {
            if (!target.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "page")
                continue;

            pages++;
            if (!target.TryGetProperty("url", out var address) ||
                address.ValueKind != JsonValueKind.String ||
                !OfficialHubPage(address.GetString()))
                continue;
            hubs++;

            Uri? endpoint = null;
            if (target.TryGetProperty("webSocketDebuggerUrl", out var websocket) &&
                websocket.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(websocket.GetString(), UriKind.Absolute, out var advertised) &&
                SafeLocalPageEndpoint(advertised))
                endpoint = advertised;
            else if (target.TryGetProperty("id", out var identifier) &&
                     identifier.ValueKind == JsonValueKind.String &&
                     System.Text.RegularExpressions.Regex.IsMatch(
                         identifier.GetString() ?? "", @"^[A-Za-z0-9-]{8,128}$"))
                endpoint = new Uri("ws://127.0.0.1:9222/devtools/page/" +
                    identifier.GetString());

            if (endpoint is null)
            {
                unreachable++;
                continue;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            await socket.ConnectAsync(endpoint, cts.Token);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var evidence = await EvaluateViaCDPAsync(
                    socket, "(" + JavaScriptHubEvidence + ")()", cts.Token);
                if (evidence.ValueKind == JsonValueKind.True)
                    return (true, "verified");
                await Task.Delay(400, cts.Token);
            }
            return (false, "hub_dashboard_not_authenticated");
        }

        return (false, pages == 0 ? "cdp_no_page_targets" :
            hubs == 0 ? "cdp_no_official_hub_page" :
            unreachable > 0 ? "cdp_hub_target_unaddressable" :
            "cdp_hub_target_not_found");
    }

    private static bool SafeLocalPageEndpoint(Uri endpoint) =>
        endpoint.Scheme == "ws" &&
        endpoint.Port == 9222 &&
        (endpoint.Host == "127.0.0.1" || endpoint.Host == "localhost") &&
        endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) &&
        endpoint.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment);

    private static async Task<JsonElement> EvaluateViaCDPAsync(
        System.Net.WebSockets.ClientWebSocket socket, string expression,
        CancellationToken cancellationToken)
    {
        var packet = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = true }
        });
        await socket.SendAsync(new ArraySegment<byte>(packet),
            System.Net.WebSockets.WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[16_384];
        using var output = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                throw new InvalidOperationException("CDP page closed during inspection");
            if (output.Length + received.Count > 1_048_576)
                throw new InvalidOperationException("CDP response too large");
            output.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
                continue;

            using var document = JsonDocument.Parse(output.ToArray());
            output.SetLength(0);
            var response = document.RootElement;
            if (!response.TryGetProperty("id", out var id) || id.GetInt32() != 1)
                continue;
            if (response.TryGetProperty("result", out var result) &&
                result.TryGetProperty("result", out var value) &&
                value.TryGetProperty("value", out var serialized))
                return serialized.Clone();
            return default;
        }
    }

    private static bool OfficialHubPage(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0)
            return false;
        if (!uri.IdnHost.Equals("maersk.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.IdnHost.Equals("www.maersk.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.AbsolutePath.Equals("/hub", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase);
    }

    // Require actual personalized Hub dashboard widgets, not just /hub/ in
    // the address bar or a generic public Maersk landing page.
    // No account-specific data is extracted or persisted.
    private const string JavaScriptHubEvidence = """
        () => {
            if (location.protocol !== 'https:' ||
                !['www.maersk.com', 'maersk.com'].includes(location.hostname) ||
                !/^\/hub(?:\/|$)/i.test(location.pathname) ||
                !document.body) return false;

            const parts = [];
            const visit = (root, depth) => {
                if (depth > 7) return;
                const elements = root.querySelectorAll('*');
                for (const el of elements) {
                    if (el.shadowRoot) visit(el.shadowRoot, depth + 1);
                }
                const txt = root === document ? document.body.innerText :
                    root.host ? root.host.shadowRoot?.textContent : '';
                if (txt && parts.length < 300) parts.push(txt.slice(0, 12000));
            };
            visit(document, 0);
            const text = parts.join('\n').replace(/\s+/g, ' ').toLowerCase();
            if (/access denied|verify you are human|captcha|required to sign in|sign in to continue/i.test(text))
                return false;
            const welcome = /welcome to your hub/i.test(text);
            const navigation = /hub dashboard/i.test(text) &&
                /manage bookings/i.test(text);
            const personalized = /pending tasks/i.test(text) &&
                /shipment overview/i.test(text);
            return welcome && navigation && personalized;
        }
        """;
}
