using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

public sealed class MaerskLoginService
{
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

        await page.GotoAsync("https://www.maersk.com/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        cancellationToken.ThrowIfCancellationRequested();

        if (await IsAuthenticatedAsync(page))
            return;

        var loginLink = page.Locator("a[href*='login'],button:has-text('Log in'),button:has-text('Login')").First;
        if (await loginLink.CountAsync() > 0)
            await loginLink.ClickAsync();

        var userInput = page.Locator("input[type='email'],input[name*='user' i],input[name*='email' i]").First;
        await userInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await userInput.FillAsync(username);

        var continueButton = page.Locator("button[type='submit'],button:has-text('Continue'),button:has-text('Next')").First;
        await continueButton.ClickAsync();

        var passwordInput = page.Locator("input[type='password']").First;
        await passwordInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await passwordInput.FillAsync(password);

        var submit = page.Locator("button[type='submit'],button:has-text('Log in'),button:has-text('Login')").First;
        await submit.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        if (!await IsAuthenticatedAsync(page))
            throw new InvalidOperationException("Maersk login did not produce an authenticated session.");
    }

    private static async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        var accountMarker = page.Locator("[data-test*='account' i],[aria-label*='account' i],a[href*='logout'],button:has-text('Log out')").First;
        return await accountMarker.CountAsync() > 0;
    }
}
