using Dhole.Agent.Infrastructure.Providers.Maersk.Browser;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

public sealed class MaerskLoginService
{
    private const string LoginUrl = "https://www.maersk.com/portaluser/login";

    private static readonly string[] UsernameSelectors =
    [
        "#mc-input-username",
        "input[autocomplete='username']",
        "input[name='username']",
        "input[id='username']",
        "input[name*='username' i]",
        "input[id*='username' i]",
        "input[placeholder*='username' i]",
        "input[aria-label*='username' i]",
        "input[type='email']"
    ];

    private static readonly string[] PasswordSelectors =
    [
        "#mc-input-password",
        "input[autocomplete='current-password']",
        "input[type='password']",
        "input[name='password']",
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

        await DismissCookieBannerAsync(page, cancellationToken);

        var usernameFilled =
            await MaerskShadowDom.FillAsync(
                page,
                UsernameSelectors,
                username,
                cancellationToken,
                timeoutMs: 12_000)
            || await MaerskShadowDom.FillMdsInputAsync(
                page,
                ["username", "user"],
                username,
                cancellationToken);

        if (!usernameFilled)
            throw await CreateLoginUiExceptionAsync(page, "username");

        var passwordFilled =
            await MaerskShadowDom.FillAsync(
                page,
                PasswordSelectors,
                password,
                cancellationToken,
                timeoutMs: 12_000)
            || await MaerskShadowDom.FillMdsInputAsync(
                page,
                ["password"],
                password,
                cancellationToken);

        if (!passwordFilled)
            throw await CreateLoginUiExceptionAsync(page, "password");

        // Prefer a real keyboard submit from the native password input. Maersk's
        // mc-button/login form has changed implementations and clicking the inner
        // shadow button can visually click without invoking the form submit handler.
        var submitted = await MaerskShadowDom.PressFirstAsync(
            page,
            PasswordSelectors,
            "Enter",
            cancellationToken,
            timeoutMs: 4_000);

        if (!submitted)
        {
            submitted =
                await MaerskShadowDom.ClickByTextAsync(
                    page,
                    ["Log in", "Login", "Sign in"],
                    cancellationToken,
                    timeoutMs: 8_000)
                || await MaerskShadowDom.ClickFirstAsync(
                    page,
                    [
                        "mc-button",
                        "button[type='submit']",
                        "input[type='submit']"
                    ],
                    cancellationToken,
                    timeoutMs: 4_000);
        }

        if (!submitted)
            throw await CreateLoginUiExceptionAsync(page, "login submit control");

        // Maersk Global Accounts can spend several seconds on the OIDC callback,
        // especially on cold browser profiles. Wait up to 60 seconds and surface
        // authentication validation/MFA messages instead of a generic UI failure.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsAuthenticatedAsync(page))
                return;

            var authMessage = await MaerskShadowDom.ReadVisibleAuthenticationMessageAsync(
                page,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(authMessage)
                && LooksLikeAuthenticationFailure(authMessage))
            {
                throw new InvalidOperationException(
                    $"Maersk rejected the login attempt: {authMessage}");
            }

            if (await RequiresInteractiveVerificationAsync(page))
            {
                throw new InvalidOperationException(
                    "Maersk requires an interactive verification step (MFA/verification code/approval) for this browser profile. " +
                    "Authenticate the persistent browser profile once, then scheduled executions can reuse the session.");
            }

            await Task.Delay(500, cancellationToken);
        }

        var finalMessage = await MaerskShadowDom.ReadVisibleAuthenticationMessageAsync(
            page,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(finalMessage))
        {
            throw new InvalidOperationException(
                $"Maersk login did not complete. Authentication page message: {finalMessage}");
        }

        throw await CreateLoginUiExceptionAsync(page, "authenticated account state");
    }

    private static async Task DismissCookieBannerAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        // The cookie banner can overlay the login card. Prefer Essential only so the
        // automation does not opt into optional tracking/marketing categories.
        await MaerskShadowDom.ClickByTextAsync(
            page,
            ["Essential only"],
            cancellationToken,
            timeoutMs: 2_000);
    }

    private static bool LooksLikeAuthenticationFailure(string message)
    {
        var value = message.ToLowerInvariant();

        return value.Contains("incorrect")
            || value.Contains("invalid")
            || value.Contains("wrong password")
            || value.Contains("unable to log")
            || value.Contains("unable to login")
            || value.Contains("account locked")
            || value.Contains("try again")
            || value.Contains("does not match")
            || value.Contains("not recognized");
    }

    private static async Task<bool> RequiresInteractiveVerificationAsync(IPage page)
    {
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync();
            var value = bodyText.ToLowerInvariant();

            return value.Contains("verification code")
                || value.Contains("verify your identity")
                || value.Contains("two-factor")
                || value.Contains("multi-factor")
                || value.Contains("authenticator")
                || value.Contains("approve sign in")
                || value.Contains("approve sign-in")
                || value.Contains("one-time password")
                || value.Contains("one time password");
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForLoginUiAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
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

            if (renderAttempt == 0
                && page.Url.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase)
                && page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
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

    private static async Task<Exception> CreateLoginUiExceptionAsync(
        IPage page,
        string missingElement)
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
            currentUrl.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase)
            && currentUrl.Contains("/auth/login", StringComparison.OrdinalIgnoreCase);

        var onPortalLogin =
            currentUrl.Contains("/portaluser/login", StringComparison.OrdinalIgnoreCase);

        if (!onAccountsLogin
            && !onPortalLogin
            && currentUrl.Contains("maersk.com", StringComparison.OrdinalIgnoreCase))
            return true;

        var accountMarker = page.Locator(
            "[data-test*='account' i]," +
            "[data-testid*='account' i]," +
            "[aria-label*='account' i]," +
            "a[href*='logout' i]," +
            "button:has-text('Log out')," +
            "button:has-text('Logout')").First;

        try
        {
            return await accountMarker.CountAsync() > 0
                   && await accountMarker.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}
