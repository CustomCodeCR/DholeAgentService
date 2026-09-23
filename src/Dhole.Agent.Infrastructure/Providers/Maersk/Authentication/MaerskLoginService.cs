using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

public sealed class MaerskLoginService
{
    private const string LoginUrl = "https://www.maersk.com/portaluser/login";
    private const float FieldTimeoutMs = 20_000;

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
        "input[name*='email' i]"
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

        // Navigate directly to the current Maersk portal login instead of relying on
        // the homepage account button, whose markup/navigation changes frequently.
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

        var userInput = await FindVisibleAsync(page, UsernameSelectors, cancellationToken);
        if (userInput is null)
            throw await CreateLoginUiExceptionAsync(page, "username");

        await userInput.FillAsync(username, new LocatorFillOptions { Timeout = FieldTimeoutMs });

        // Maersk has used both a single-page username/password form and a two-step
        // username -> Continue -> password flow. Support both.
        var passwordInput = await FindVisibleAsync(page, PasswordSelectors, cancellationToken, timeoutMs: 2_000);
        if (passwordInput is null)
        {
            var continueButton = await FindVisibleAsync(
                page,
                [
                    "button:has-text('Continue')",
                    "button:has-text('Next')",
                    "button[type='submit']",
                    "input[type='submit']"
                ],
                cancellationToken,
                timeoutMs: 5_000);

            if (continueButton is null)
                throw await CreateLoginUiExceptionAsync(page, "continue button");

            await continueButton.ClickAsync(new LocatorClickOptions { Timeout = FieldTimeoutMs });

            passwordInput = await FindVisibleAsync(page, PasswordSelectors, cancellationToken);
            if (passwordInput is null)
                throw await CreateLoginUiExceptionAsync(page, "password");
        }

        await passwordInput.FillAsync(password, new LocatorFillOptions { Timeout = FieldTimeoutMs });

        var submit = await FindVisibleAsync(
            page,
            [
                "button:has-text('Log in')",
                "button:has-text('Login')",
                "button:has-text('Sign in')",
                "button[type='submit']",
                "input[type='submit']"
            ],
            cancellationToken,
            timeoutMs: 10_000);

        if (submit is null)
            throw await CreateLoginUiExceptionAsync(page, "login button");

        await submit.ClickAsync(new LocatorClickOptions { Timeout = FieldTimeoutMs });

        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            // Some Maersk SPA transitions do not produce a traditional document load.
            // Authentication is verified below from the resulting page state.
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await IsAuthenticatedAsync(page))
        {
            // Give the SPA a short opportunity to complete redirects/account hydration.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(500, cancellationToken);
                if (await IsAuthenticatedAsync(page))
                    return;
            }

            throw await CreateLoginUiExceptionAsync(page, "authenticated account state");
        }
    }

    private static async Task<ILocator?> FindVisibleAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        CancellationToken cancellationToken,
        float timeoutMs = FieldTimeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var selector in selectors)
            {
                var locator = page.Locator(selector).First;
                if (await locator.CountAsync() == 0)
                    continue;

                try
                {
                    if (await locator.IsVisibleAsync())
                        return locator;
                }
                catch (PlaywrightException)
                {
                    // DOM may be replacing the login component while redirects finish.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    private static async Task<Exception> CreateLoginUiExceptionAsync(IPage page, string missingElement)
    {
        var title = string.Empty;
        try
        {
            title = await page.TitleAsync();
        }
        catch (PlaywrightException)
        {
            // Best-effort diagnostic only.
        }

        var detectedInputs = new List<string>();
        try
        {
            var inputs = page.Locator("input");
            var count = Math.Min(await inputs.CountAsync(), 12);

            for (var i = 0; i < count; i++)
            {
                var input = inputs.Nth(i);
                var type = await input.GetAttributeAsync("type") ?? string.Empty;
                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                var autocomplete = await input.GetAttributeAsync("autocomplete") ?? string.Empty;
                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;

                detectedInputs.Add(
                    $"type={Sanitize(type)},name={Sanitize(name)},id={Sanitize(id)},autocomplete={Sanitize(autocomplete)},placeholder={Sanitize(placeholder)}");
            }
        }
        catch (PlaywrightException)
        {
            // Best-effort diagnostic only.
        }

        var inputsDescription = detectedInputs.Count == 0
            ? "none"
            : string.Join(" | ", detectedInputs);

        return new InvalidOperationException(
            $"Maersk login could not find the {missingElement}. URL='{page.Url}', Title='{Sanitize(title)}', Inputs=[{inputsDescription}].");
    }

    private static string Sanitize(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        // If the browser has already left the login route, also look for known account
        // controls. This keeps persistent Playwright profiles reusable between executions.
        var accountMarker = page.Locator(
            "[data-test*='account' i]," +
            "[data-testid*='account' i]," +
            "[aria-label*='account' i]," +
            "a[href*='logout' i]," +
            "button:has-text('Log out')," +
            "button:has-text('Logout')").First;

        if (await accountMarker.CountAsync() > 0)
        {
            try
            {
                if (await accountMarker.IsVisibleAsync())
                    return true;
            }
            catch (PlaywrightException)
            {
                // Continue with URL/form checks.
            }
        }

        var onLoginRoute =
            page.Url.Contains("/portaluser/login", StringComparison.OrdinalIgnoreCase) ||
            page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase);

        if (!onLoginRoute)
        {
            var password = page.Locator("input[type='password']").First;
            if (await password.CountAsync() == 0)
                return true;
        }

        return false;
    }
}
