using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

public sealed class MaerskBrowserAutomation
{
    public async Task FillSearchAsync(IPage page, MaerskSearchInput input, CancellationToken cancellationToken, string? searchUrl = null)
    {
        if (string.IsNullOrWhiteSpace(searchUrl))
            throw new InvalidOperationException(
                "The selected extraction profile does not define a Maersk search URL.");

        var targetUrl = searchUrl.Trim();
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"The selected extraction profile contains an invalid Maersk search URL: '{targetUrl}'.");

        await page.GotoAsync(
            targetUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });

        cancellationToken.ThrowIfCancellationRequested();

        await FillFirstAsync(
            page,
            [
                "input[name*='origin' i]",
                "input[placeholder*='origin' i]",
                "input[aria-label*='origin' i]",
                "input[name*='from' i]",
                "input[placeholder*='from' i]"
            ],
            ["origin", "from"],
            input.Pol,
            cancellationToken);

        await SelectSuggestionAsync(page, cancellationToken);

        await FillFirstAsync(
            page,
            [
                "input[name*='destination' i]",
                "input[placeholder*='destination' i]",
                "input[aria-label*='destination' i]",
                "input[name*='to' i]",
                "input[placeholder*='to' i]"
            ],
            ["destination", "to"],
            input.Pod,
            cancellationToken);

        await SelectSuggestionAsync(page, cancellationToken);

        await FillFirstAsync(
            page,
            ["input[name*='weight' i]", "input[aria-label*='weight' i]"],
            ["weight"],
            input.WeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken,
            required: false);

        await FillFirstAsync(
            page,
            ["input[name*='commodity' i]", "input[aria-label*='commodity' i]"],
            ["commodity"],
            input.Commodity,
            cancellationToken,
            required: false);

        var date = input.CargoReadyDate.ToString("yyyy-MM-dd");
        await FillFirstAsync(
            page,
            ["input[type='date']", "input[name*='date' i]", "input[aria-label*='date' i]"],
            ["date", "cargo ready"],
            date,
            cancellationToken,
            required: false);

        await MaerskShadowDom.SelectOptionByLabelAsync(
            page,
            [
                "select[name*='equipment' i]",
                "select[aria-label*='equipment' i]",
                "select[name*='container' i]",
                "select[aria-label*='container' i]"
            ],
            input.ContainerType,
            cancellationToken,
            timeoutMs: 4_000);

        await FillFirstAsync(
            page,
            ["input[name*='quantity' i]", "input[aria-label*='quantity' i]"],
            ["quantity"],
            input.Quantity.ToString(),
            cancellationToken,
            required: false);

        var submitted =
            await MaerskShadowDom.ClickByTextAsync(
                page,
                ["Search", "Get prices", "Find prices", "Show prices"],
                cancellationToken,
                timeoutMs: 10_000)
            || await MaerskShadowDom.ClickFirstAsync(
                page,
                ["button[type='submit']", "input[type='submit']"],
                cancellationToken,
                timeoutMs: 3_000);

        if (!submitted)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk price search button was not found. URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }
    }

    private static async Task FillFirstAsync(
        IPage page,
        string[] selectors,
        string[] semanticNames,
        string value,
        CancellationToken cancellationToken,
        bool required = true)
    {
        var filled =
            await MaerskShadowDom.FillAsync(page, selectors, value, cancellationToken, timeoutMs: required ? 12_000 : 2_500)
            || await MaerskShadowDom.FillMdsInputAsync(page, semanticNames, value, cancellationToken);

        if (!filled && required)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Required Maersk search field was not found for '{string.Join("/", semanticNames)}'. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }
    }

    private static async Task SelectSuggestionAsync(IPage page, CancellationToken cancellationToken)
    {
        // Suggestions may also be rendered from an MDS component's shadow root.
        await MaerskShadowDom.ClickFirstAsync(
            page,
            [
                "[role='option']",
                "li[role='option']",
                "[data-test*='suggestion' i]",
                "[data-testid*='suggestion' i]"
            ],
            cancellationToken,
            timeoutMs: 5_000);
    }
}
