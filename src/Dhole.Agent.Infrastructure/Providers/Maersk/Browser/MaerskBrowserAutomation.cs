using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

public sealed class MaerskBrowserAutomation
{
    public async Task FillSearchAsync(
        IPage page,
        MaerskSearchInput input,
        CancellationToken cancellationToken,
        string? searchUrl = null,
        bool navigateToSearchUrl = true)
    {
        if (string.IsNullOrWhiteSpace(searchUrl))
            throw new InvalidOperationException(
                "The selected extraction profile does not define a Maersk search URL.");

        var targetUrl = searchUrl.Trim();
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"The selected extraction profile contains an invalid Maersk search URL: '{targetUrl}'.");

        if (navigateToSearchUrl)
        {
            await page.GotoAsync(
                targetUrl,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });

            cancellationToken.ThrowIfCancellationRequested();
        }

        await FillFirstAsync(
            page,
            [
                "#mc-input-origin",
                "input#mc-input-origin",
                "mc-c-origin-destination #mc-input-origin",
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
                "#mc-input-destination",
                "input#mc-input-destination",
                "mc-c-origin-destination #mc-input-destination",
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
            [
                "mc-c-commodity input[placeholder*='minimum 2 characters' i]",
                "mc-c-commodity input[type='text']",
                "input[name*='commodity' i]",
                "input[aria-label*='commodity' i]"
            ],
            ["commodity"],
            input.Commodity,
            cancellationToken,
            required: false);

        await SelectSuggestionAsync(page, cancellationToken);

        var date = input.CargoReadyDate.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        await FillFirstAsync(
            page,
            [
                "#mc-input-earliestDepartureDatePicker",
                "input#mc-input-earliestDepartureDatePicker",
                "input[name='earliestDepartureDatePicker']",
                "input[type='date']",
                "input[name*='date' i]",
                "input[placeholder*='DD MMM YYYY' i]",
                "input[aria-label*='date' i]"
            ],
            ["date", "cargo ready"],
            date,
            cancellationToken,
            required: false);

        var equipmentSelected = await MaerskShadowDom.SelectOptionByLabelAsync(
            page,
            [
                "select[name*='equipment' i]",
                "select[aria-label*='equipment' i]",
                "select[name*='container' i]",
                "select[aria-label*='container' i]"
            ],
            input.ContainerType,
            cancellationToken,
            timeoutMs: 2_000);

        if (!equipmentSelected)
        {
            var equipmentFilled = await MaerskShadowDom.FillAsync(
                page,
                [
                    "input[name='containerSelect']",
                    "input[placeholder*='container type and size' i]",
                    "mc-c-container-select input[name='containerSelect']",
                    "mc-c-container-selection-input input[name='containerSelect']"
                ],
                input.ContainerType,
                cancellationToken,
                timeoutMs: 5_000);

            if (equipmentFilled)
                await SelectSuggestionAsync(page, cancellationToken);
        }

        var quantityFilled = await MaerskShadowDom.FillAsync(
            page,
            [
                "input[name='containers']",
                "input[placeholder*='number of containers' i]",
                "mc-c-container-select input[name='containers']",
                "mc-c-container-selection-input input[name='containers']"
            ],
            input.Quantity.ToString(),
            cancellationToken,
            timeoutMs: 3_000);

        if (quantityFilled)
            await SelectSuggestionAsync(page, cancellationToken);

        // In the current Maersk booking UI the cargo-weight input remains disabled
        // until container type/size and quantity have been selected.
        var weightSelectors = new[]
        {
            "#mc-input-weight",
            "input#mc-input-weight",
            "input[name='weight']",
            "input[name*='weight' i]",
            "input[placeholder*='cargo weight' i]",
            "input[aria-label*='weight' i]"
        };

        var weightFilled = await MaerskShadowDom.FillAsync(
            page,
            weightSelectors,
            input.WeightKg.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken,
            timeoutMs: 10_000);

        if (!weightFilled)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk cargo weight field did not become enabled/editable after selecting container and quantity. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

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
        // Maersk displays an informational coachmark over the location suggestions
        // on fresh/updated booking sessions. It is not part of the search flow and
        // can intercept pointer events even though the mc-option itself is visible.
        await MaerskShadowDom.DismissBlockingCoachmarksAsync(
            page,
            cancellationToken);

        var selectors = new[]
        {
            "mc-option",
            "mc-list mc-option",
            "[role='option']",
            "li[role='option']",
            "[data-test*='suggestion' i]",
            "[data-testid*='suggestion' i]"
        };

        // Prefer a normal user-like click. If Maersk's informational overlay is
        // still transitioning, force-click only the already-visible suggestion.
        var selected = await MaerskShadowDom.ClickFirstAsync(
            page,
            selectors,
            cancellationToken,
            timeoutMs: 1_500);

        if (!selected)
        {
            await MaerskShadowDom.ClickFirstAsync(
                page,
                selectors,
                cancellationToken,
                timeoutMs: 2_000,
                force: true);
        }
    }
}
