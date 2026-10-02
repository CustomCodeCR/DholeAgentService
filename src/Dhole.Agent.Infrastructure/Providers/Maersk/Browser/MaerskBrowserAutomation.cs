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

        var originSelectors = new[]
        {
            "#mc-input-origin",
            "input#mc-input-origin",
            "mc-c-origin-destination #mc-input-origin",
            "input[name*='origin' i]",
            "input[placeholder*='origin' i]",
            "input[aria-label*='origin' i]",
            "input[name*='from' i]",
            "input[placeholder*='from' i]"
        };

        await FillAndSelectLocationAsync(
            page,
            originSelectors,
            ["origin", "from"],
            "origin",
            input.Pol,
            input.PolCode,
            cancellationToken);

        var destinationSelectors = new[]
        {
            "#mc-input-destination",
            "input#mc-input-destination",
            "mc-c-origin-destination #mc-input-destination",
            "input[name*='destination' i]",
            "input[placeholder*='destination' i]",
            "input[aria-label*='destination' i]",
            "input[name*='to' i]",
            "input[placeholder*='to' i]"
        };

        await FillAndSelectLocationAsync(
            page,
            destinationSelectors,
            ["destination", "to"],
            "destination",
            input.Pod,
            input.PodCode,
            cancellationToken);

        // Best-effort CY/CY selection. Maersk sometimes keeps Container Yard as
        // the default state without exposing a clickable radio/button. The actual
        // readiness check is the commodity control becoming enabled below.
        await MaerskShadowDom.SelectContainerYardServiceModesAsync(
            page,
            cancellationToken,
            timeoutMs: 8_000);

        var commoditySelectors = new[]
        {
            "mc-c-commodity input[placeholder*='minimum 2 characters' i]",
            "mc-c-commodity input[type='text']",
            "input[name*='commodity' i]",
            "input[aria-label*='commodity' i]"
        };

        var commodityFilled = await MaerskShadowDom.FillAsync(
            page,
            commoditySelectors,
            input.Commodity,
            cancellationToken,
            timeoutMs: 10_000);

        if (!commodityFilled)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk commodity field did not become enabled after POL/POD and CY/CY resolution. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

        var commoditySelected = await MaerskShadowDom.SelectTypeaheadOptionAsync(
            page,
            [input.Commodity],
            cancellationToken,
            timeoutMs: 4_000,
            exactOnly: true);

        if (!commoditySelected)
        {
            var moved = await MaerskShadowDom.PressFirstAsync(
                page,
                commoditySelectors,
                "ArrowDown",
                cancellationToken,
                timeoutMs: 2_000);

            commoditySelected = moved && await MaerskShadowDom.PressFirstAsync(
                page,
                commoditySelectors,
                "Enter",
                cancellationToken,
                timeoutMs: 2_000);
        }

        if (!commoditySelected)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk commodity '{input.Commodity}' could not be selected. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

        var equipmentInputSelectors = new[]
        {
            "input[name='containerSelect']",
            "input[placeholder*='container type and size' i]",
            "mc-c-container-select input[name='containerSelect']",
            "mc-c-container-selection-input input[name='containerSelect']"
        };

        var equipmentDisplayLabel = GetEquipmentDisplayLabel(input.ContainerType);
        var equipmentCode = GetEquipmentSearchTerm(input.ContainerType);

        // The current Maersk UI renders this as a custom dropdown. The visible
        // labels are "20 Dry Standard", "40 Dry Standard", "40 Dry High", etc.,
        // while the committed native value is 20DV/40DV/40HC.
        await MaerskShadowDom.ClickFirstAsync(
            page,
            equipmentInputSelectors,
            cancellationToken,
            timeoutMs: 3_000);

        var equipmentChosen = await MaerskShadowDom.SelectContainerTypeAsync(
            page,
            equipmentDisplayLabel,
            equipmentCode,
            cancellationToken,
            timeoutMs: 6_000);

        if (!equipmentChosen)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk container type '{input.ContainerType}' could not be selected from the booking options. " +
                $"Expected visible label='{equipmentDisplayLabel}', code='{equipmentCode}'. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

        var quantitySelectors = new[]
        {
            "input[name='containers']",
            "input[placeholder*='number of containers' i]",
            "mc-c-container-select input[name='containers']",
            "mc-c-container-selection-input input[name='containers']"
        };

        var quantityText = input.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var quantityChosen = await MaerskShadowDom.SetContainerQuantityAsync(
            page,
            quantitySelectors,
            input.Quantity,
            cancellationToken,
            timeoutMs: 6_000);

        if (!quantityChosen)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk container quantity '{quantityText}' could not be set after selecting '{equipmentDisplayLabel}'. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

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

        var priceOwnerSelected = await MaerskShadowDom.SelectPriceOwnerAsync(
            page,
            cancellationToken,
            timeoutMs: 5_000);

        if (!priceOwnerSelected)
        {
            var diagnostics = await MaerskShadowDom.DescribeAsync(page);
            throw new InvalidOperationException(
                $"Maersk price owner option 'I am the price owner' could not be selected. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }

        var date = input.CargoReadyDate.ToString(
            "dd MMM yyyy",
            System.Globalization.CultureInfo.InvariantCulture);

        var dateSelected = false;

        if (input.CargoReadyDate == Dhole.Agent.Application.Agents.MaerskExecutionDefaults.GetCargoReadyDate())
        {
            dateSelected = await MaerskShadowDom.ClickByTextAsync(
                page,
                ["Select tomorrow"],
                cancellationToken,
                timeoutMs: 4_000);
        }

        if (!dateSelected)
        {
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
                cancellationToken);
        }

        var submitted =
            await MaerskShadowDom.ClickByTextAsync(
                page,
                ["Continue", "Continue to book", "Search", "Get prices", "Find prices", "Show prices"],
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
                $"Maersk Continue/Search action did not become enabled after completing the booking fields. " +
                $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
        }
    }

    private static async Task FillAndSelectLocationAsync(
        IPage page,
        string[] selectors,
        string[] semanticNames,
        string componentId,
        string displayValue,
        string? locationCode,
        CancellationToken cancellationToken)
    {
        // Preserve the proven path first: use the configured human label exactly
        // as provided, then its locality portion. Generic aliases are only
        // fallbacks, so adding new route formats cannot regress routes that
        // already work.
        var searchTerms = GetLocationSearchTerms(displayValue);

        foreach (var searchTerm in searchTerms)
        {
            var typed = await MaerskShadowDom.TypeAsync(
                page,
                selectors,
                searchTerm,
                cancellationToken,
                timeoutMs: 8_000,
                delayMs: 55);

            if (!typed)
            {
                await FillFirstAsync(
                    page,
                    selectors,
                    semanticNames,
                    searchTerm,
                    cancellationToken);
            }

            var matchValues = GetLocationMatchValues(displayValue, searchTerm);

            var suggestionReady = await MaerskShadowDom.WaitForLocationSuggestionAsync(
                page,
                componentId,
                matchValues,
                cancellationToken,
                timeoutMs: 7_000);

            if (!suggestionReady)
                continue;

            await MaerskShadowDom.DismissBlockingCoachmarksAsync(
                page,
                cancellationToken);

            // First try the configured label exactly. This is the last-known-good
            // behavior for existing routes.
            var selected = await MaerskShadowDom.ClickVisibleLocationOptionAsync(
                page,
                componentId,
                displayValue,
                "CY",
                cancellationToken,
                timeoutMs: 4_000);

            // Only if the exact configured representation is not present, try
            // aliases derived from the same value. No UN/LOCODE-specific mapping
            // is required.
            if (!selected)
            {
                foreach (var matchValue in matchValues
                             .Where(x => !string.Equals(
                                 x,
                                 displayValue,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    selected = await MaerskShadowDom.ClickVisibleLocationOptionAsync(
                        page,
                        componentId,
                        matchValue,
                        "CY",
                        cancellationToken,
                        timeoutMs: 2_000);

                    if (selected)
                        break;
                }
            }

            // Keyboard navigation remains the final fallback, and is attempted
            // only after Maersk has positively announced matching suggestions.
            if (!selected)
            {
                var moved = await MaerskShadowDom.PressFirstAsync(
                    page,
                    selectors,
                    "ArrowDown",
                    cancellationToken,
                    timeoutMs: 2_000);

                selected = moved && await MaerskShadowDom.PressFirstAsync(
                    page,
                    selectors,
                    "Enter",
                    cancellationToken,
                    timeoutMs: 2_000);
            }

            if (!selected)
                continue;

            // Validate against the configured label first, then generic aliases.
            if (await MaerskShadowDom.WaitForResolvedLocationValueAsync(
                    page,
                    componentId,
                    searchTerm,
                    displayValue,
                    cancellationToken,
                    timeoutMs: 4_000))
                return;

            foreach (var matchValue in matchValues
                         .Where(x => !string.Equals(
                             x,
                             displayValue,
                             StringComparison.OrdinalIgnoreCase)))
            {
                if (await MaerskShadowDom.WaitForResolvedLocationValueAsync(
                        page,
                        componentId,
                        searchTerm,
                        matchValue,
                        cancellationToken,
                        timeoutMs: 1_500))
                    return;
            }
        }

        var diagnostics = await MaerskShadowDom.DescribeAsync(page);
        throw new InvalidOperationException(
            $"Maersk location '{displayValue}' (code='{locationCode ?? "-"}') could not be committed for '{componentId}'. " +
            $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
    }

    private static string[] GetLocationSearchTerms(string displayValue)
    {
        if (string.IsNullOrWhiteSpace(displayValue))
            return [];

        var trimmed = CollapseLocationWhitespace(displayValue);
        var commaParts = trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var locality = commaParts.Length > 0
            ? commaParts[0]
            : trimmed;

        var parenthesisIndex = locality.IndexOf('(');
        var localityWithoutParenthesis = parenthesisIndex > 0
            ? locality[..parenthesisIndex].Trim()
            : locality;

        var genericWords = new HashSet<string>(
            [
                "PORT",
                "PUERTO",
                "HARBOR",
                "HARBOUR",
                "TERMINAL",
                "CITY",
                "OF",
                "DE",
                "DEL",
                "THE"
            ],
            StringComparer.OrdinalIgnoreCase);

        var meaningfulTokens = localityWithoutParenthesis
            .Split(
                [' ', '-', '/', '_'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length >= 3 && !genericWords.Contains(token))
            .ToArray();

        var simplifiedLocality = meaningfulTokens.Length > 0
            ? string.Join(" ", meaningfulTokens)
            : localityWithoutParenthesis;

        var anchor = meaningfulTokens
            .OrderByDescending(token => token.Length)
            .FirstOrDefault();

        var shortPrefix = anchor is { Length: >= 6 }
            ? anchor[..4]
            : null;

        // Order matters: the two first entries are the proven behavior. The rest
        // exist only to support newly configured route labels automatically.
        return new[]
            {
                trimmed,
                locality,
                localityWithoutParenthesis,
                simplifiedLocality,
                anchor,
                shortPrefix
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetLocationMatchValues(
        string displayValue,
        string searchTerm)
    {
        var trimmed = CollapseLocationWhitespace(displayValue);
        var commaParts = trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var countryOrTail = commaParts.Length > 1
            ? commaParts[^1]
            : string.Empty;

        var locality = commaParts.Length > 0
            ? commaParts[0]
            : trimmed;

        var parenthesisIndex = locality.IndexOf('(');
        if (parenthesisIndex > 0)
            locality = locality[..parenthesisIndex].Trim();

        var search = CollapseLocationWhitespace(searchTerm);

        // Keep the full configured label first. Country/tail-qualified aliases
        // prevent similarly named locations in another country from being chosen.
        return new[]
            {
                trimmed,
                countryOrTail.Length > 0 ? $"{locality}, {countryOrTail}" : locality,
                countryOrTail.Length > 0 ? $"{search}, {countryOrTail}" : search,
                locality,
                search
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CollapseLocationWhitespace(string value)
        => string.Join(
            " ",
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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

    private static async Task<bool> SelectSuggestionAsync(
        IPage page,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? expectedValues = null,
        bool exactOnly = false,
        bool allowFirstFallback = true)
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

        if (expectedValues is { Count: > 0 })
        {
            var matched = await MaerskShadowDom.ClickVisibleOptionMatchingAsync(
                page,
                selectors,
                expectedValues,
                cancellationToken,
                timeoutMs: 2_500,
                exactOnly: exactOnly);

            if (matched)
                return true;
        }

        if (!allowFirstFallback)
            return false;

        // Generic typeaheads such as ports/commodity can still fall back to the
        // first visible result if Maersk renders a descriptive label that differs
        // from the typed search value.
        var selected = await MaerskShadowDom.ClickFirstAsync(
            page,
            selectors,
            cancellationToken,
            timeoutMs: 1_500);

        if (selected)
            return true;

        return await MaerskShadowDom.ClickFirstAsync(
            page,
            selectors,
            cancellationToken,
            timeoutMs: 2_000,
            force: true);
    }

    private static string GetEquipmentDisplayLabel(string containerType)
    {
        var key = NormalizeEquipmentKey(containerType);

        return key switch
        {
            "20DRYSTANDARD20DV" or "22G1" => "20 Dry Standard",
            "40DRYSTANDARD40DV" or "42G1" => "40 Dry Standard",
            "40DRYHIGH40HC" or "45G1" => "40 Dry High",
            "45DRYHIGH45HC" or "L5G1" => "45 Dry High",
            "20TANK" or "22T3" => "20 Tank",
            "40TANK" or "42T3" => "40 Tank",
            "20REEFERSTANDARD20NOR" or "22R1" => "20 Reefer Standard",
            "40REEFERHIGH40NOR" or "45R1" => "40 Reefer High",
            "40REEFERSTANDARD" or "42R1" => "40 Reefer Standard",
            "20OPENTOP20OT" or "22U1" => "20 Open Top",
            "40OPENTOP40OT" or "42U1" => "40 Open Top",
            "40OPENTOPHIGH" or "45U1" => "40 Open Top High",
            "40FLATSTANDARD" or "42P3" => "40 Flat Standard",
            "40FLATHIGH" or "45P3" => "40 Flat High",
            "20FLAT" or "22P1" => "20 Flat",
            _ => containerType.Trim()
        };
    }

    private static string GetEquipmentSearchTerm(string containerType)
    {
        var key = NormalizeEquipmentKey(containerType);

        return key switch
        {
            "20DRYSTANDARD20DV" or "22G1" => "22G1",
            "40DRYSTANDARD40DV" or "42G1" => "42G1",
            "40DRYHIGH40HC" or "45G1" => "45G1",
            "45DRYHIGH45HC" or "L5G1" => "L5G1",
            "20TANK" or "22T3" => "22T3",
            "40TANK" or "42T3" => "42T3",
            "20REEFERSTANDARD20NOR" or "22R1" => "22R1",
            "40REEFERHIGH40NOR" or "45R1" => "45R1",
            "40REEFERSTANDARD" or "42R1" => "42R1",
            "20OPENTOP20OT" or "22U1" => "22U1",
            "40OPENTOP40OT" or "42U1" => "42U1",
            "40OPENTOPHIGH" or "45U1" => "45U1",
            "40FLATSTANDARD" or "42P3" => "42P3",
            "40FLATHIGH" or "45P3" => "45P3",
            "20FLAT" or "22P1" => "22P1",
            _ => containerType.Trim()
        };
    }

    private static string NormalizeEquipmentKey(string value)
        => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string[] GetEquipmentAliases(string containerType)
        => [containerType];
}
