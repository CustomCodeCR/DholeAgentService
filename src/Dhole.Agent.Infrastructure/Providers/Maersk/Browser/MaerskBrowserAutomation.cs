using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

public sealed class MaerskBrowserAutomation
{
    public async Task FillSearchAsync(IPage page, MaerskSearchInput input, CancellationToken cancellationToken)
    {
        await page.GotoAsync("https://www.maersk.com/instant-prices/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        cancellationToken.ThrowIfCancellationRequested();

        await FillFirstAsync(page, ["input[name*='origin' i]","input[placeholder*='origin' i]","input[aria-label*='origin' i]"], input.Pol);
        await SelectSuggestionAsync(page);

        await FillFirstAsync(page, ["input[name*='destination' i]","input[placeholder*='destination' i]","input[aria-label*='destination' i]"], input.Pod);
        await SelectSuggestionAsync(page);

        await FillFirstAsync(page, ["input[name*='weight' i]","input[aria-label*='weight' i]"], input.WeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture), required:false);
        await FillFirstAsync(page, ["input[name*='commodity' i]","input[aria-label*='commodity' i]"], input.Commodity, required:false);

        var date = input.CargoReadyDate.ToString("yyyy-MM-dd");
        await FillFirstAsync(page, ["input[type='date']","input[name*='date' i]"], date, required:false);

        var equipment = page.Locator("select[name*='equipment' i],select[aria-label*='equipment' i]").First;
        if (await equipment.CountAsync() > 0)
        {
            try { await equipment.SelectOptionAsync(new[] { new SelectOptionValue { Label = input.ContainerType } }); }
            catch { }
        }

        var quantity = page.Locator("input[name*='quantity' i],input[aria-label*='quantity' i]").First;
        if (await quantity.CountAsync() > 0)
            await quantity.FillAsync(input.Quantity.ToString());

        var search = page.Locator("button[type='submit'],button:has-text('Search'),button:has-text('Get prices'),button:has-text('Find prices')").Last;
        await search.ClickAsync();
    }

    private static async Task FillFirstAsync(IPage page, string[] selectors, string value, bool required=true)
    {
        foreach (var selector in selectors)
        {
            var locator=page.Locator(selector).First;
            if (await locator.CountAsync() == 0) continue;
            await locator.FillAsync(value);
            return;
        }
        if (required) throw new InvalidOperationException($"Required Maersk search field was not found for value '{value}'.");
    }

    private static async Task SelectSuggestionAsync(IPage page)
    {
        var suggestion=page.Locator("[role='option'],li[role='option'],[data-test*='suggestion' i]").First;
        if (await suggestion.CountAsync()>0)
            await suggestion.ClickAsync();
    }
}
