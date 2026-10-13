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
        CancellationToken cancellationToken,
        string? configuredLoginUrl = null,
        string? authenticationSuccessUrl = null,
        string? configuredSearchUrl = null,
        bool preferInteractiveAuthentication = false)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Maersk username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Maersk password is required.", nameof(password));

        var network = new AuthenticationNetworkObserver(page);
        network.Attach();

        try
        {
            // Prefer the persistent browser session before submitting credentials.
            // Going directly to /portaluser/login on every scheduled run can force
            // Global Accounts to create a new session and invalidate a perfectly
            // usable OIDC/browser session.
            if (await TryResumeExistingSessionAsync(
                    page,
                    configuredSearchUrl,
                    authenticationSuccessUrl,
                    cancellationToken))
                return;

            await page.GotoAsync(
                ResolveLoginUrl(configuredLoginUrl),
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });

            cancellationToken.ThrowIfCancellationRequested();

            await CompleteAuthenticatedContinueAsync(page, cancellationToken);

            if (await IsAuthenticatedAsync(page, authenticationSuccessUrl))
                return;

            if (!await WaitForLoginUiAsync(page, authenticationSuccessUrl, cancellationToken))
                throw await CreateLoginUiExceptionAsync(
                    page,
                    "rendered login form",
                    network);

            await DismissCookieBannerAsync(page, cancellationToken);

            if (preferInteractiveAuthentication)
            {
                var interactiveAuthenticated =
                    await WaitForInteractiveAuthenticationAsync(
                        page,
                        configuredSearchUrl,
                        authenticationSuccessUrl,
                        TimeSpan.FromMinutes(10),
                        cancellationToken);

                if (interactiveAuthenticated)
                    return;

                throw new MaerskAuthenticationException(
                    "maersk_authentication_verification_required",
                    "Maersk requires the persistent browser profile to be authenticated interactively. " +
                    "The browser was kept open for 10 minutes without completing authentication. " +
                    "Open the worker noVNC console through the SSH tunnel, sign in to Maersk manually, then run the schedule again.");
            }

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
                throw await CreateLoginUiExceptionAsync(
                    page,
                    "username",
                    network);

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
                throw await CreateLoginUiExceptionAsync(
                    page,
                    "password",
                    network);

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
                throw await CreateLoginUiExceptionAsync(
                    page,
                    "login submit control",
                    network);

            for (var attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await CompleteAuthenticatedContinueAsync(page, cancellationToken);

                if (await IsAuthenticatedAsync(page, authenticationSuccessUrl))
                    return;

                // Do not abort on the first 401/403 emitted by the Global Accounts SPA.
                // Maersk can issue transient authentication/resource failures while the
                // browser is still completing its client-side login/callback flow.
                // A visible authentication error remains terminal below; otherwise let
                // the browser finish the full authentication window before classifying
                // the captured network failures.
                var authMessage = await MaerskShadowDom.ReadVisibleAuthenticationMessageAsync(
                    page,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(authMessage)
                    && LooksLikeAuthenticationFailure(authMessage))
                {
                    throw network.CreateException(
                        $"Maersk rejected the login attempt: {authMessage}");
                }

                if (await RequiresInteractiveVerificationAsync(page))
                {
                    throw new MaerskAuthenticationException(
                        "maersk_authentication_verification_required",
                        "Maersk requires an interactive verification step (MFA/verification code/approval) for this browser profile. " +
                        "Authenticate the persistent browser profile once, then scheduled executions can reuse the session.");
                }

                await Task.Delay(500, cancellationToken);
            }

            // A login submission can leave Global Accounts displaying a generic
            // SPA error even though cookies/session state were partially established.
            // Re-enter the configured booking URL once and let the normal
            // Continue -> callback -> /book/ handoff prove whether the session works.
            if (await TryResumeExistingSessionAsync(
                    page,
                    configuredSearchUrl,
                    authenticationSuccessUrl,
                    cancellationToken))
                return;

            var finalMessage = await MaerskShadowDom.ReadVisibleAuthenticationMessageAsync(
                page,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                throw network.CreateException(
                    $"Maersk login did not complete. Authentication page message: {finalMessage}");
            }

            if (network.HasBlockingHttpStatus)
            {
                throw network.CreateException(
                    "Maersk did not complete authentication after the browser login window.");
            }

            throw await CreateLoginUiExceptionAsync(
                page,
                "authenticated account state",
                network);
        }
        finally
        {
            network.Detach();
        }
    }

    /// <summary>
    /// Recovery requires stronger evidence than visiting Maersk's homepage:
    /// an edge-denied page can retain the /book URL with no authenticated user.
    /// Do not mark BrowserProfile.Authenticated unless the account control is
    /// actually visible on the original provider homepage or booking page.
    /// </summary>
    public static async Task<bool> HasVerifiedBookingSessionAsync(
        IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MaerskBookingAccountEvidence.IsOfficialSessionCheckPage(page.Url))
            return false;

        var body = page.Locator("body").First;
        try
        {
            var content = (await body.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 4_000 }))
                .Trim();
            if (content.StartsWith("Access Denied", StringComparison.OrdinalIgnoreCase)
                || content.Contains("You don't have permission to access", StringComparison.OrdinalIgnoreCase))
                return false;

            var account = page.Locator(
                "[data-test*='account' i], [data-testid*='account' i], " +
                "[aria-label*='account' i], a[href*='logout' i], " +
                "button:has-text('Log out'), button:has-text('Logout')").First;
            if (await account.CountAsync() > 0 && await account.IsVisibleAsync())
                return true;

            // Current Maersk booking UI renders an initials avatar beside a
            // visible given/family name instead of an account/logout locator.
            // Find BOTH within the right side of the top navigation and require
            // their initials to match. Do not accept prices, the /book URL by
            // itself, or unrelated public navigation text as proof of login.
            // Only this boolean evidence is used to update the session state;
            // account names are neither logged nor persisted by this probe.
            var identityEvidence = await page.EvaluateAsync<string?>( """
                () => {
                    const visible = element => {
                        const r = element.getBoundingClientRect();
                        if (!r.width || !r.height || r.bottom <= 0
                            || r.top >= 340 || r.right < innerWidth * 0.55)
                            return false;
                        const css = getComputedStyle(element);
                        return css.display !== 'none' && css.visibility !== 'hidden';
                    };
                    const avatars = [], names = [], codes = [], codeLabels = [];
                    const namePattern = /\b[\p{Lu}][\p{Ll}]{1,34}\s+[\p{Lu}][\p{Ll}]{1,34}\b/gu;
                    const visit = root => {
                        for (const el of root.querySelectorAll('*')) {
                            if (el.shadowRoot) visit(el.shadowRoot);
                            if (!visible(el)) continue;
                            const r = el.getBoundingClientRect();
                            const content = (el.textContent || '').trim();
                            if (content.length > 160) continue;
                            if (/^[A-Z]{2}$/.test(content)
                                && r.width <= 85 && r.height <= 85)
                                avatars.push({ value: content, r });
                            else if (/^[0-9]{8,15}$/.test(content) && r.top >= 150
                                     && r.width <= 240 && r.height <= 90)
                                codes.push({ r });
                            else if (/^(?:customer|client)\s*code\s*:?$/i.test(content)
                                     && r.top >= 150 && r.width <= 240)
                                codeLabels.push({ r });
                            else {
                                for (const match of content.matchAll(namePattern)) {
                                    names.push({ value: match[0], r });
                                    if (names.length >= 100) break;
                                }
                            }
                            if (avatars.length > 100 || names.length > 100
                                || codes.length > 100 || codeLabels.length > 100) break;
                        }
                    };
                    visit(document);
                    for (const avatar of avatars) {
                        for (const name of names) {
                            const words = name.value.split(/\s+/);
                            if (name.r.left >= avatar.r.left - 20
                                && name.r.left <= avatar.r.right + 300
                                && Math.abs(name.r.top - avatar.r.top) <= 75
                                && (words[0][0] + words[1][0]).toUpperCase()
                                   === avatar.value)
                                return avatar.value + '|' + name.value;
                        }
                    }
                    // Maersk's account menu can expose a customer code instead
                    // of logout/account controls. The code is evidence ONLY
                    // alongside a visible account avatar and its nearby label,
                    // inside the upper-right profile menu on the official site.
                    // Return a constant token, never the actual customer code.
                    for (const avatar of avatars) {
                        for (const code of codes) {
                            if (code.r.left < avatar.r.left - 20
                                || code.r.left > avatar.r.right + 350
                                || code.r.top < avatar.r.top - 25
                                || code.r.top > avatar.r.bottom + 160)
                                continue;
                            if (codeLabels.some(label =>
                                Math.abs(label.r.top - code.r.top) <= 85
                                && Math.abs(label.r.left - code.r.left) <= 180))
                                return 'VERIFIED_CUSTOMER_CODE';
                        }
                    }
                    return null;
                }
                """ );

            if (string.IsNullOrEmpty(identityEvidence)) return false;
            if (identityEvidence == "VERIFIED_CUSTOMER_CODE") return true;
            var separator = identityEvidence.IndexOf('|');
            return separator == 2
                   && MaerskBookingAccountEvidence.AvatarMatchesDisplayName(
                       identityEvidence[..separator], identityEvidence[(separator + 1)..]);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> TryResumeExistingSessionAsync(
        IPage page,
        string? configuredSearchUrl,
        string? authenticationSuccessUrl,
        CancellationToken cancellationToken)
    {
        var searchUrl = ResolveSearchUrl(configuredSearchUrl);

        try
        {
            await page.GotoAsync(
                searchUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
        }
        catch (PlaywrightException)
        {
            // Redirects during the OIDC handoff can replace the document while
            // Playwright is waiting. Inspect the resulting URL/state below.
        }

        var stableSearchPolls = 0;

        for (var poll = 0; poll < 120; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentUrl = page.Url;

            if (MatchesConfiguredUrl(currentUrl, authenticationSuccessUrl))
            {
                stableSearchPolls = 0;
                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (currentUrl.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase))
            {
                stableSearchPolls = 0;

                try
                {
                    await CompleteAuthenticatedContinueAsync(page, cancellationToken);
                }
                catch (MaerskAuthenticationException ex)
                    when (ex.ErrorCode is "maersk_authentication_continue_not_clickable"
                        or "maersk_authentication_callback_timeout")
                {
                    // If the actual login fields are present, this is not an
                    // authenticated Continue page; fall through to credential login.
                }

                if (await HasLoginFieldsAsync(page, cancellationToken))
                    return false;

                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (currentUrl.Contains("/portaluser/login", StringComparison.OrdinalIgnoreCase))
            {
                stableSearchPolls = 0;

                if (await HasLoginFieldsAsync(page, cancellationToken))
                    return false;

                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (MatchesConfiguredUrlOrChild(currentUrl, searchUrl))
            {
                stableSearchPolls++;
                if (stableSearchPolls >= 12)
                    return true;
            }
            else
            {
                stableSearchPolls = 0;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> WaitForInteractiveAuthenticationAsync(
        IPage page,
        string? configuredSearchUrl,
        string? authenticationSuccessUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var searchUrl = ResolveSearchUrl(configuredSearchUrl);
        var stableSearchPolls = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await CompleteAuthenticatedContinueAsync(page, cancellationToken);
            }
            catch (MaerskAuthenticationException ex)
                when (ex.ErrorCode is "maersk_authentication_continue_not_clickable"
                    or "maersk_authentication_callback_timeout")
            {
                // The operator may still be typing credentials, solving an
                // interactive check, or waiting for the accounts SPA to finish.
            }

            var currentUrl = page.Url;

            if (MatchesConfiguredUrl(currentUrl, authenticationSuccessUrl))
            {
                stableSearchPolls = 0;
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var onAccounts =
                currentUrl.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase);
            var onPortalLogin =
                currentUrl.Contains("/portaluser/login", StringComparison.OrdinalIgnoreCase);

            if (!onAccounts
                && !onPortalLogin
                && MatchesConfiguredUrlOrChild(currentUrl, searchUrl))
            {
                stableSearchPolls++;
                if (stableSearchPolls >= 6)
                    return true;
            }
            else
            {
                stableSearchPolls = 0;
            }

            if (await IsAuthenticatedAsync(page, authenticationSuccessUrl)
                && !onAccounts
                && !onPortalLogin)
                return true;

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private static Task<bool> HasLoginFieldsAsync(
        IPage page,
        CancellationToken cancellationToken)
        => MaerskShadowDom.HasVisibleAsync(
            page,
            UsernameSelectors.Concat(PasswordSelectors).ToArray(),
            cancellationToken);

    internal static async Task CompleteAuthenticatedContinueAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        if (!page.Url.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase))
            return;

        string bodyText;
        try
        {
            bodyText = await page.Locator("body").InnerTextAsync();
        }
        catch (PlaywrightException)
        {
            return;
        }

        if (!bodyText.Contains("You are authenticated", StringComparison.OrdinalIgnoreCase))
            return;

        var attemptedClick = false;

        async Task<bool> WaitForHandoffAsync()
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!page.Url.Contains("accounts.maersk.com", StringComparison.OrdinalIgnoreCase))
                    return true;

                await Task.Delay(250, cancellationToken);
            }

            return false;
        }

        var clicked = await MaerskShadowDom.ClickByTextAsync(
            page,
            ["Continue"],
            cancellationToken,
            timeoutMs: 2_000);

        attemptedClick |= clicked;
        if (clicked && await WaitForHandoffAsync())
            return;

        clicked = await MaerskShadowDom.ClickFirstAsync(
            page,
            [
                "mc-button:has-text('Continue')",
                "mc-button[label='Continue']",
                "mc-button[aria-label='Continue']",
                "button:has-text('Continue')",
                "a:has-text('Continue')",
                "[role='button']:has-text('Continue')",
                "[role='link']:has-text('Continue')"
            ],
            cancellationToken,
            timeoutMs: 4_000);

        attemptedClick |= clicked;
        if (clicked && await WaitForHandoffAsync())
            return;

        throw new MaerskAuthenticationException(
            attemptedClick
                ? "maersk_authentication_callback_timeout"
                : "maersk_authentication_continue_not_clickable",
            attemptedClick
                ? "Maersk confirmed the browser session is authenticated, but Continue did not complete the OIDC callback navigation."
                : "Maersk confirmed the browser session is authenticated, but the Continue control could not be activated.");
    }

    private static async Task DismissCookieBannerAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        await MaerskShadowDom.ClickByTextAsync(
            page,
            ["Essential only"],
            cancellationToken,
            timeoutMs: 2_000);
    }

    private static bool LooksLikeAuthenticationFailure(string message)
    {
        var value = message.ToLowerInvariant();

        // Only credential/account-specific messages are terminal here.
        // Global Accounts can transiently render generic messages such as
        // "Something went wrong [object Object]" while its SPA/session/token
        // requests are still progressing. Those are classified only after the
        // full browser authentication window if the flow never completes.
        return value.Contains("incorrect password")
            || value.Contains("invalid password")
            || value.Contains("wrong password")
            || value.Contains("invalid username")
            || value.Contains("invalid email")
            || value.Contains("account locked")
            || value.Contains("does not match")
            || value.Contains("not recognized")
            || value.Contains("unknown user")
            || value.Contains("user does not exist");
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
        string? authenticationSuccessUrl,
        CancellationToken cancellationToken)
    {
        for (var renderAttempt = 0; renderAttempt < 2; renderAttempt++)
        {
            for (var poll = 0; poll < 40; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await IsAuthenticatedAsync(page, authenticationSuccessUrl))
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
        string missingElement,
        AuthenticationNetworkObserver network)
    {
        var diagnostics = await MaerskShadowDom.DescribeAsync(page);
        return network.CreateException(
            $"Maersk login could not find or complete the {missingElement}. " +
            $"URL='{SanitizeUrl(page.Url)}'. ShadowDOM diagnostics={diagnostics}");
    }

    private static async Task<bool> IsAuthenticatedAsync(
        IPage page,
        string? authenticationSuccessUrl)
    {
        var currentUrl = page.Url;

        // The OIDC callback is an intermediate state. Returning authenticated here
        // can interrupt the callback before Maersk persists the browser session.
        if (MatchesConfiguredUrl(currentUrl, authenticationSuccessUrl))
            return false;

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

    private static string ResolveSearchUrl(string? configuredSearchUrl)
    {
        if (Uri.TryCreate(configuredSearchUrl, UriKind.Absolute, out var configured)
            && (configured.Scheme == Uri.UriSchemeHttp || configured.Scheme == Uri.UriSchemeHttps)
            && configured.Host.EndsWith("maersk.com", StringComparison.OrdinalIgnoreCase)
            && !configured.Host.Equals("api.maersk.com", StringComparison.OrdinalIgnoreCase))
            return configured.ToString();

        return "https://www.maersk.com/book/";
    }

    private static bool MatchesConfiguredUrlOrChild(string currentUrl, string configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var expected)
            || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            || !current.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedPath = expected.AbsolutePath.TrimEnd('/');
        var currentPath = current.AbsolutePath.TrimEnd('/');

        return currentPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(expectedPath)
                && currentPath.StartsWith(expectedPath + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveLoginUrl(string? configuredLoginUrl)
    {
        if (Uri.TryCreate(configuredLoginUrl, UriKind.Absolute, out var configured)
            && (configured.Scheme == Uri.UriSchemeHttp || configured.Scheme == Uri.UriSchemeHttps)
            && (configured.Host.Equals("maersk.com", StringComparison.OrdinalIgnoreCase)
                || configured.Host.EndsWith(".maersk.com", StringComparison.OrdinalIgnoreCase)))
        {
            // Persisted OIDC URLs embed a one-time nonce and PKCE challenge.
            // Reusing those URLs on a scheduled run can break the callback.
            // Start at Maersk's stable login entry point instead.
            if (configured.AbsolutePath.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
                && (configured.Query.Contains("code_challenge=", StringComparison.OrdinalIgnoreCase)
                    || configured.Query.Contains("nonce=", StringComparison.OrdinalIgnoreCase)
                    || configured.Query.Contains("state=", StringComparison.OrdinalIgnoreCase)))
                return LoginUrl;

            return configured.ToString();
        }

        return LoginUrl;
    }

    private static bool MatchesConfiguredUrl(string currentUrl, string? configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl)
            || !Uri.TryCreate(configuredUrl, UriKind.Absolute, out var expected)
            || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return false;

        return current.Scheme.Equals(expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && current.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
            && current.AbsolutePath.TrimEnd('/').Equals(expected.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return value.Split('?', 2)[0];

        return uri.GetLeftPart(UriPartial.Path);
    }

    private sealed class AuthenticationNetworkObserver(IPage page)
    {
        private readonly object _sync = new();
        private readonly List<HttpFailure> _failures = [];
        private bool _attached;

        public bool HasBlockingHttpStatus
        {
            get
            {
                lock (_sync)
                {
                    return _failures.Any(IsBlockingAuthenticationFailure);
                }
            }
        }

        public void Attach()
        {
            if (_attached)
                return;

            page.Response += OnResponse;
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached)
                return;

            page.Response -= OnResponse;
            _attached = false;
        }

        public MaerskAuthenticationException CreateException(string message)
        {
            HttpFailure[] failures;

            lock (_sync)
                failures = _failures.ToArray();

            var sessionFailure = failures
                .LastOrDefault(x =>
                    x.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    && x.Url.Contains("/sessions/", StringComparison.OrdinalIgnoreCase));

            var edgeDenied = failures.Any(IsEdgeAccessDenied);

            var code = edgeDenied
                ? "maersk_authentication_edge_denied"
                : failures.Any(x => x.Status == 429)
                    ? "maersk_authentication_rate_limited"
                    : sessionFailure?.Status == 401
                        ? "maersk_authentication_unauthorized"
                        : sessionFailure?.Status == 403
                            ? "maersk_authentication_forbidden"
                            : failures.Any(x => x.Status == 401 && IsAuthenticationEndpoint(x))
                                ? "maersk_authentication_unauthorized"
                                : failures.Any(x => x.Status == 403 && IsAuthenticationEndpoint(x))
                                    ? "maersk_authentication_forbidden"
                                    : failures.Any(x => x.Status >= 500 && IsAuthenticationEndpoint(x))
                                        ? "maersk_authentication_service_error"
                                        : "maersk_authentication_failed";

            var summary = failures.Length == 0
                ? "No HTTP 401/403/429 response was captured from Maersk authentication endpoints."
                : "HTTP failures: " + string.Join(
                    " | ",
                    failures
                        .TakeLast(8)
                        .Select(x => string.IsNullOrWhiteSpace(x.Detail)
                            ? $"{x.Method} {x.Status} {x.Url}"
                            : $"{x.Method} {x.Status} {x.Url} response='{x.Detail}'"));

            return new MaerskAuthenticationException(
                code,
                $"{message} {summary}");
        }

        private static bool IsBlockingAuthenticationFailure(HttpFailure failure)
            => failure.Status == 429
               || IsEdgeAccessDenied(failure)
               || ((failure.Status is 401 or 403) && IsAuthenticationEndpoint(failure));

        private static bool IsEdgeAccessDenied(HttpFailure failure)
        {
            if (string.IsNullOrWhiteSpace(failure.Detail))
                return false;

            var detail = failure.Detail.ToLowerInvariant();

            return detail.Contains("access denied")
                || detail.Contains("edgesuite.net")
                || detail.Contains("you don't have permission to access")
                || detail.Contains("reason=unauthorized")
                || (detail.Contains("denied")
                    && (detail.Contains("permission")
                        || detail.Contains("permi")
                        || detail.Contains("edge")));
        }

        private static bool IsAuthenticationEndpoint(HttpFailure failure)
            => failure.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
               || failure.Url.Contains("/sessions/", StringComparison.OrdinalIgnoreCase)
               || failure.Url.Contains("/auth/", StringComparison.OrdinalIgnoreCase)
               || failure.Url.Contains("/login", StringComparison.OrdinalIgnoreCase)
               || failure.Url.Contains("/oauth", StringComparison.OrdinalIgnoreCase)
               || failure.Url.Contains("/token", StringComparison.OrdinalIgnoreCase);

        private async void OnResponse(object? sender, IResponse response)
        {
            if (response.Status < 400)
                return;

            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var uri)
                || !uri.Host.EndsWith("maersk.com", StringComparison.OrdinalIgnoreCase))
                return;

            var method = response.Request.Method;
            var url = uri.GetLeftPart(UriPartial.Path);
            string? detail = null;

            if (IsAuthenticationEndpoint(new HttpFailure(method, response.Status, url, null)))
            {
                try
                {
                    detail = SanitizeAuthenticationFailureBody(await response.TextAsync());
                }
                catch (PlaywrightException)
                {
                    // Response body may no longer be available after navigation.
                }
            }

            var failure = new HttpFailure(
                method,
                response.Status,
                url,
                detail);

            lock (_sync)
            {
                var existingIndex = _failures.FindIndex(x =>
                    x.Method == failure.Method
                    && x.Status == failure.Status
                    && x.Url == failure.Url);

                if (existingIndex >= 0)
                {
                    if (string.IsNullOrWhiteSpace(_failures[existingIndex].Detail)
                        && !string.IsNullOrWhiteSpace(failure.Detail))
                        _failures[existingIndex] = failure;

                    return;
                }

                _failures.Add(failure);

                if (_failures.Count > 20)
                    _failures.RemoveAt(0);
            }
        }

        private static string? SanitizeAuthenticationFailureBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var text = body
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                var root = document.RootElement;
                var safe = new List<string>();

                foreach (var key in new[] { "error", "error_description", "message", "reason" })
                {
                    if (!root.TryGetProperty(key, out var value))
                        continue;

                    var item = value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? value.GetString()
                        : value.ToString();

                    if (!string.IsNullOrWhiteSpace(item))
                        safe.Add($"{key}={item}");
                }

                if (safe.Count > 0)
                    text = string.Join("; ", safe);
            }
            catch (System.Text.Json.JsonException)
            {
                // Keep a short plain-text description below.
            }

            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(?i)(access_token|refresh_token|id_token|authorization|password|code)s*[:=]s*[""']?[^,""'s}]+",
                "$1=[redacted]");

            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"s+",
                " ").Trim();

            return text.Length <= 500 ? text : text[..500];
        }

        private sealed record HttpFailure(
            string Method,
            int Status,
            string Url,
            string? Detail);
    }
}

public sealed class MaerskAuthenticationException(
    string errorCode,
    string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
