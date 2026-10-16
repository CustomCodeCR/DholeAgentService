using System.Text.Json;
using Microsoft.Playwright;

namespace Dhole.Agent.Workers;

/// <summary>
/// Read-only proof of an authorized Maersk Hub account in the isolated standby browser.
/// No credentials, cookies, navigation, CAPTCHA interaction or queue services.
/// Called only via docker exec in the dormant generation's browser-only container.
/// </summary>
internal static class StandbyAccountInspector
{
    public sealed record Identity(string DisplayName, string Username, string CustomerId);

    public static async Task<int> RunAsync()
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectOverCDPAsync(
                "http://127.0.0.1:9222", new BrowserTypeConnectOverCDPOptions
                {
                    Timeout = 8_000
                });

            foreach (var context in browser.Contexts)
            foreach (var page in context.Pages)
            {
                if (!AllowedOfficialPage(page.Url))
                    continue;

                var identity = await page.EvaluateAsync<Identity?>(JavaScriptEvidence);
                if (identity is null || !ValidIdentity(identity))
                    continue;

                // This is captured by the protected workflow and stored privately.
                // The workflow must never print the stdout or account details.
                Console.WriteLine(JsonSerializer.Serialize(identity));
                return 0;
            }
        }
        catch (PlaywrightException)
        {
            // Deliberately do not disclose page content or provider token data.
        }
        catch (TimeoutException)
        {
        }
        Console.Error.WriteLine("STANDBY_MAERSK_ACCOUNT_NOT_VERIFIED");
        return 3;
    }

    private static bool AllowedOfficialPage(string? location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0)
            return false;

        if (!uri.IdnHost.Equals("www.maersk.com", StringComparison.OrdinalIgnoreCase)
            && !uri.IdnHost.Equals("maersk.com", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.AbsolutePath == "/"
            || uri.AbsolutePath.Equals("/hub", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("/book", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/book/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidIdentity(Identity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.DisplayName) ||
            identity.DisplayName.Length is < 4 or > 100 ||
            identity.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            return false;
        if (identity.DisplayName.Any(ch => char.IsControl(ch) || ch is '\'' or '"'))
            return false;
        if (identity.Username.Length is < 3 or > 64 ||
            !identity.Username.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-'))
            return false;
        return identity.CustomerId.Length is >= 8 and <= 15
            && identity.CustomerId.All(ch => ch is >= '0' and <= '9');
    }

    private const string JavaScriptEvidence = """
        () => {
            // Only a visible, expanded Account menu is evidence of authentication.
            // The public Maersk homepage, prices and generic avatar are insufficient.
            const body = document.body;
            if (!body) return null;
            const text = (body.innerText || '').replace(/\r/g, '');
            if (!/(?:^|\n)\s*Account\s*(?:\n|$)/i.test(text)
                || !/(?:^|\n)\s*Log out\s*(?:\n|$)/i.test(text)
                || /Access Denied|Verify you are human|captcha required/i.test(text))
                return null;
            const grab = (label, pattern) => {
                const lines = text.split('\n').map(line => line.trim()).filter(Boolean);
                const position = lines.findIndex(line => line.toLowerCase() === label.toLowerCase());
                if (position < 0 || position >= lines.length - 1) return null;
                const value = lines[position + 1];
                return pattern.test(value) ? value : null;
            };
            const displayName = grab('Name', /^[\p{L}][\p{L} .'-]{3,99}$/u);
            const customerId = grab('Customer ID', /^[0-9]{8,15}$/);
            const username = grab('Username', /^[A-Za-z0-9_.-]{3,64}$/);
            if (!displayName || !customerId || !username) return null;
            return { displayName, username, customerId };
        }
        """;
}
