using Dhole.Agent.Infrastructure.Providers.Maersk.Browser;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

public sealed class MaerskLoginService
{
    private const string LoginUrl = "https://www.maersk.com/portaluser/login";

    private static readonly string[] UsernameSelectors =
    [
        "input[autocomplete='username']",
        "input[name='username']",
        "input[id='username']",
        "input[name*='username' i]",
        "input[id*='username' i]",
        "input[placeholder*='username' i]",
        "input[aria-label*='username' i]",
        "input[name*='user' i]",
        "input[id*='user' i]",
        "input[type='email']",
        "input[name*='email' i]",
        "input:not([type='password']):not([type='hidden']):not([type='checkbox']):not([type='radio']):not([type='submit']):not([disabled])"
    ];

    private static readonly string[] PasswordSelectors =
    [
        "input[autocomplete='current-password']",
        "input[type='password']",
        "input[name*='password' i]",
        "input[id*='password' i]",
        "input[aria-label*='password' i]"
    ];

    public async Task EnsureAuthenticatedAsync(
        IPage page,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Maersk username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Maersk password is required.", nameof(password));

        await page.GotoAsync(
            LoginUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });

        cancellationToken.ThrowIfCancellationRequested();

        if (await IsAuthenticatedAsync(page))
            return;

        if (!await WaitForLoginUiAsync(page, cancellationToken))
            throw await CreateLoginUiExceptionAsync(page, "rendered login form");

        var usernameFilled =
            await MaerskShadowDom.FillAsync(page, UsernameSelectors, username, cancellationToken, timeoutMs: 12_000)
            || await MaerskShadowDom.FillMdsInputAsync(page, ["username", "user"], username, cancellationToken);

        if (!usernameFilled)
            throw await CreateLoginUiExceptionAsync(page, "username");

        // The current Maersk Global Accounts screen shows username and password
        // together, but keep the two-step flow as a compatibility fallback.
        var passwordFilled =
            await MaerskShadowDom.FillAsync(page, PasswordSelectors, password, cancellationToken, timeoutMs: 3_000)
            || await MaerskShadowDom.FillMdsInputAsync(page, ["password"], password, cancellationToken);

        if (!passwordFilled)
        {
            var advanced =
                await MaerskShadowDom.ClickByTextAsync(page, ["Continue", "Next"], cancellationToken, timeoutMs: 5_000)
                || await MaerskShadowDom.ClickFirstAsync(
                    page,
                    ["button[type='submit']", "input[type='submit']"],
                    cancellationToken,
                    timeoutMs: 2_000);

            if (!advanced)
                throw await CreateLoginUiExceptionAsync(page, "password/continue control");

            passwordFilled =
                await MaerskShadowDom.FillAsync(page, PasswordSelectors, password, cancellationToken, timeoutMs: 12_000)
                || await MaerskShadowDom.FillMdsInputAsync(page, ["password"], password, cancellationToken);
        }

        if (!passwordFilled)
            throw await CreateLoginUiExceptionAsync(page, "password");

        var submitted =
            await MaerskShadowDom.ClickByTextAsync(page, ["Log in", "Login", "Sign in"], cancellationToken, timeoutMs: 8_000)
            || await MaerskShadowDom.ClickFirstAsync(
                page,
                ["button[type='submit']", "input[type='submit']"],
                cancellationToken,
                timeoutMs: 3_000);

        if (!submitted)
            throw await CreateLoginUiExceptionAsync(page, "login button");

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsAuthenticatedAsync(page))
                return;

            await Task.Delay(500, cancellationToken);
        }

        throw await CreateLoginUiExceptionAsync(page, "authenticated account state");
    }

    private static async Task<bool> WaitForLoginUiAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        // Maersk Global Accounts is a JavaScript application. DOMContentLoaded can
        // complete before the IAM bundle has mounted the form, especially after the
        // portaluser -> accounts.maersk.com redirect.
        for (var renderAttempt = 0; renderAttempt < 2; renderAttempt++)
        {
            for (var poll = 0; poll < 40; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await IsAuthenticatedAsync(page))
                    return true;

                if (await MaerskShadowDom.HasVisibleAsync(
                        page,
                        UsernameSelectors.Concat(PasswordSelectors).ToArray(),
                        cancellationToken))
                    return true;

                await Task.Delay(500, cancellationToken);
            }

            if (renderAttempt == 0 &&
                page.Url.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase) &&
                page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await page.ReloadAsync(
                        new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 60_000
                        });
                }
                catch (PlaywrightException)
                {
                    // Continue polling. A redirect/reload can race the SPA bootstrap.
                }
            }
        }

        return false;
    }

    private static async Task<Exception> CreateLoginUiExceptionAsync(IPage page, string missingElement)
    {
        var diagnostics = await MaerskShadowDom.DescribeAsync(page);
        return new InvalidOperationException(
            $"Maersk login could not find or complete the {missingElement}. " +
            $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
    }

    private static async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        var currentUrl = page.Url;

        var onAccountsLogin =
            currentUrl.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase) &&
            currentUrl.Contains("/auth/login", StringComparison.OrdinalIgnoreCase);

        var onPortalLogin =
            currentUrl.Contains("/portaluser/login", StringComparison.OrdinalIgnoreCase);

        if (!onAccountsLogin && !onPortalLogin &&
            currentUrl.Contains("maersk.com", StringComparison.OrdinalIgnoreCase))
            return true;

        // Persistent profiles may already be authenticated while still resolving redirects.
        var accountMarker = page.Locator(
            "[data-test*='account' i]," +
            "[data-testid*='account' i]," +
            "[aria-label*='account' i]," +
            "a[href*='logout' i]," +
            "button:has-text('Log out')," +
            "button:has-text('Logout')").First;

        try
        {
            return await accountMarker.CountAsync() > 0 && await accountMarker.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}
