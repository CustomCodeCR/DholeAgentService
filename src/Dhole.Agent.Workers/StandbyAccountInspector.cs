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
        var reason = "no_official_hub_page";
        try
        {
            using var playwright = await Playwright.CreateAsync();
            // Never close externally owned Chromium or navigate the browser.
            // Inspection only reads the protected Hub and opens its Account panel.
            var browser = await playwright.Chromium.ConnectOverCDPAsync(
                "http://127.0.0.1:9222", new BrowserTypeConnectOverCDPOptions
                {
                    Timeout = 8_000
                });

            foreach (var context in browser.Contexts)
            foreach (var page in context.Pages)
            {
                if (!AllowedOfficialPage(page.Url) ||
                    !Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ||
                    !(uri.AbsolutePath.Equals("/hub", StringComparison.OrdinalIgnoreCase) ||
                      uri.AbsolutePath.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase)))
                    continue;

                reason = "hub_identity_not_visible";
                var identity = await ReadIdentityAsync(page);
                if (identity is null)
                {
                    // Only a fixed set of structural reasons is exposed.
                    // Never dump DOM text, account names, identifiers or tokens.
                    reason = await page.EvaluateAsync<string>(JavaScriptDiagnostic);
                    continue;
                }

                if (!ValidIdentity(identity))
                {
                    reason = "hub_identity_invalid_format";
                    continue;
                }

                // Identity details are sent only to the protected workflow via stdout.
                // No page HTML, cookies, authentication headers or tokens are logged.
                Console.WriteLine(JsonSerializer.Serialize(identity));
                return 0;
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

        Console.Error.WriteLine("STANDBY_MAERSK_ACCOUNT_NOT_VERIFIED reason=" + reason);
        return 3;
    }

    private static async Task<Identity?> ReadIdentityAsync(IPage page)
    {
        // Maersk's account trigger can be a button, an icon with an accessible
        // name, or a text-bearing element inside a web component. Read the
        // panel first, since opening an already expanded menu may close it.
        var candidates = new ILocator[]
        {
            page.GetByRole(AriaRole.Button, new() { Name = "Account", Exact = true }),
            page.Locator("button[aria-label*='account' i], [role='button'][aria-label*='account' i]"),
            page.GetByText("Account", new() { Exact = true })
        };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var identity = await page.EvaluateAsync<Identity?>(JavaScriptEvidence);
            if (identity is not null && ValidIdentity(identity))
                return identity;

            if (attempt == 0)
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        if (await candidate.CountAsync() == 0)
                            continue;
                        await candidate.First.ClickAsync(new() { Timeout = 1_500 });
                        break;
                    }
                    catch (PlaywrightException)
                    {
                        // A hidden candidate should not prevent trying the next
                        // visible control. Never force-click or navigate.
                    }
                }
            }
            await Task.Delay(450);
        }

        // Preserve the distinction between absent and malformed account data.
        return await page.EvaluateAsync<Identity?>(JavaScriptEvidence);
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
        if (identity.DisplayName.Any(ch => char.IsControl(ch) || ch is '"'))
            return false;
        if (identity.Username is null || identity.Username.Length is < 3 or > 128 ||
            !(System.Text.RegularExpressions.Regex.IsMatch(identity.Username, @"^[A-Za-z0-9_.-]{3,64}$") ||
              System.Text.RegularExpressions.Regex.IsMatch(identity.Username, @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")))
            return false;
        return identity.CustomerId.Length is >= 8 and <= 15
            && identity.CustomerId.All(ch => ch is >= '0' and <= '9');
    }

    private const string JavaScriptDiagnostic = """
        () => {
            if (!/^\/hub(?:\/|$)/i.test(location.pathname))
                return 'not_on_official_hub';
            const lines = [];
            const record = value => {
                if (!value) return;
                for (const part of value.split('\n')) {
                    const line = part.trim().toLowerCase().replace(/:$/, '');
                    if (line && line.length < 180) lines.push(line);
                }
            };
            record(document.body?.innerText || '');
            const walk = (root, depth) => {
                if (depth > 8) return;
                for (const el of root.querySelectorAll('*')) {
                    if (!el.shadowRoot) continue;
                    for (const node of el.shadowRoot.querySelectorAll('*')) {
                        if (!node.children.length && node.getClientRects().length)
                            record(node.textContent || '');
                    }
                    walk(el.shadowRoot, depth + 1);
                }
            };
            walk(document, 0);
            if (!lines.length) return 'hub_dom_empty';
            if (lines.some(x => /access denied|verify you are human|captcha required/.test(x)))
                return 'hub_challenge_detected';
            const has = (...aliases) => lines.some(line =>
                aliases.some(alias => line === alias || line.startsWith(alias + ':')));
            if (!has('name', 'full name', 'display name')) return 'hub_name_label_missing';
            if (!has('username', 'user name')) return 'hub_username_label_missing';
            if (!has('customer id', 'customer number')) return 'hub_customer_label_missing';
            return 'hub_identity_values_not_recognized';
        }
        """;

    private const string JavaScriptEvidence = """
        () => {
            // An official protected Hub plus a visible real account identity
            // is required; the URL by itself is never sufficient to attest
            // the specific credential. Do not read cookies or browser storage.
            if (!/^\/hub(?:\/|$)/i.test(location.pathname)) return null;
            if (!document.body) return null;

            const prohibited = /Access Denied|Verify you are human|captcha required|Sign in to continue/i;
            const entries = [];
            const addText = (value) => {
                if (!value) return;
                for (const row of value.replace(/\r/g, '').split('\n')) {
                    const text = row.replace(/\s+/g, ' ').trim();
                    if (text && text.length <= 180) entries.push(text);
                }
            };
            addText(document.body.innerText || '');

            // Walk *open* shadow roots of Maersk web components. Standard
            // document.body.innerText does not include their rendered text.
            const walk = (root, depth) => {
                if (depth > 8) return;
                for (const el of root.querySelectorAll('*')) {
                    if (!el.shadowRoot) continue;
                    addText(el.shadowRoot.innerText || '');
                    for (const node of el.shadowRoot.querySelectorAll('*')) {
                        if (node.children.length === 0 && node.getClientRects().length)
                            addText(node.innerText || node.textContent || '');
                    }
                    walk(el.shadowRoot, depth + 1);
                }
            };
            walk(document, 0);
            if (prohibited.test(entries.join('\n'))) return null;

            const labels = {
                DisplayName: ['Name', 'Full name', 'Display name'],
                Username: ['Username', 'User name'],
                CustomerId: ['Customer ID', 'Customer Id', 'Customer number']
            };
            const valid = {
                DisplayName: /^[\p{L}][\p{L} .'-]{3,99}$/u,
                Username: /^(?:[A-Za-z0-9_.-]{3,64}|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})$/,
                CustomerId: /^[0-9]{8,15}$/
            };
            const fields = {};
            for (const [field, aliases] of Object.entries(labels)) {
                fields[field] = '';
                for (let i = 0; i < entries.length && !fields[field]; i++) {
                    for (const alias of aliases) {
                        const line = entries[i];
                        const exact = line.toLowerCase().replace(/:$/, '') === alias.toLowerCase();
                        const inline = line.toLowerCase().startsWith(alias.toLowerCase() + ':');
                        const value = (exact ? entries[i + 1] : inline
                            ? line.slice(alias.length + 1).trim() : '') || '';
                        if (valid[field].test(value)) {
                            fields[field] = value;
                            break;
                        }
                    }
                }
            }
            if (!fields.DisplayName || !fields.Username || !fields.CustomerId)
                return null;
            return fields;
        }
        """;
}
