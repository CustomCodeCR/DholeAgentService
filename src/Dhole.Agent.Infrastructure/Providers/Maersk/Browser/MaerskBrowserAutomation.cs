using Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;
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
            // Maersk MDS controls increasingly ignore synthetic HTMLElement.click().
            // Use a real Playwright pointer click for "Select tomorrow".
            dateSelected = await MaerskShadowDom.ClickVisibleActionByTextAsync(
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

        var offerRequestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<IRequest>? requestHandler = null;
        requestHandler = (_, request) =>
        {
            if (request.Url.Contains(
                    "/v2/departures/offers",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    request.Method,
                    "POST",
                    StringComparison.OrdinalIgnoreCase))
            {
                offerRequestStarted.TrySetResult(true);
            }
        };

        page.Request += requestHandler;

        try
        {
            var submitLabels = new[]
            {
                "Continue to book",
                "Continue",
                "Search",
                "Get prices",
                "Find prices",
                "Show prices"
            };

            // First choice: trusted Playwright click. This is deliberately not
            // the generic JS click because Maersk can report a synthetic click as
            // successful while never issuing departures/offers.
            var submitted =
                await MaerskShadowDom.ClickVisibleActionByTextAsync(
                    page,
                    submitLabels,
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

            var firstSignal = await Task.WhenAny(
                offerRequestStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(12), cancellationToken));

            if (firstSignal != offerRequestStarted.Task)
            {
                var captchaChallenge =
                    await MaerskShadowDom.ReadInteractiveHcaptchaChallengeAsync(
                        page,
                        cancellationToken);

                if (!string.IsNullOrWhiteSpace(captchaChallenge))
                {
                    throw new MaerskAuthenticationException(
                        "maersk_hcaptcha_required",
                        "Maersk presented an interactive hCaptcha challenge after the booking form was submitted. " +
                        "The persistent browser profile was preserved and scheduled searches must stop until the verification is completed interactively. " +
                        $"Challenge='{captchaChallenge}'");
                }

                // The control was clickable but the SPA did not submit. Retry once
                // with a forced trusted pointer event against the live control.
                await MaerskShadowDom.ClickVisibleActionByTextAsync(
                    page,
                    submitLabels,
                    cancellationToken,
                    timeoutMs: 5_000,
                    force: true);

                var retrySignal = await Task.WhenAny(
                    offerRequestStarted.Task,
                    Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));

                if (retrySignal != offerRequestStarted.Task)
                {
                    captchaChallenge =
                        await MaerskShadowDom.ReadInteractiveHcaptchaChallengeAsync(
                            page,
                            cancellationToken);

                    if (!string.IsNullOrWhiteSpace(captchaChallenge))
                    {
                        throw new MaerskAuthenticationException(
                            "maersk_hcaptcha_required",
                            "Maersk presented an interactive hCaptcha challenge after the booking form was submitted. " +
                            "The persistent browser profile was preserved and scheduled searches must stop until the verification is completed interactively. " +
                            $"Challenge='{captchaChallenge}'");
                    }

                    var diagnostics = await MaerskShadowDom.DescribeAsync(page);
                    throw new InvalidOperationException(
                        $"Maersk booking form was completed, but Continue did not issue POST /v2/departures/offers. " +
                        $"URL='{page.Url}'. ShadowDOM diagnostics={diagnostics}");
                }
            }
        }
        finally
        {
            page.Request -= requestHandler;
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
        var searchTerms = GetLocationSearchTerms(displayValue);

        foreach (var searchTerm in searchTerms)
        {
            // The current Maersk booking typeahead expects the city name only.
            // Example: typing "Shanghai" shows the first result as
            // "Shanghai (Shanghai), China — Container Yard".
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

            var suggestionReady = await MaerskShadowDom.WaitForLocationSuggestionAsync(
                page,
                componentId,
                [displayValue],
                cancellationToken,
                timeoutMs: 7_000);

            if (!suggestionReady)
                continue;

            await MaerskShadowDom.DismissBlockingCoachmarksAsync(
                page,
                cancellationToken);

            // Use a real Playwright click on the Maersk location option.
            // Calling HTMLElement.click() on this MDS web component can report
            // success without committing the internal typeahead selection.
            // Prefer the CY option explicitly and require the complete configured
            // city/country tokens, so "Puerto Caldera, Costa Rica" cannot fall
            // back to Caldera, Chile or to the Store Door variant.
            var selected = await MaerskShadowDom.ClickVisibleLocationOptionAsync(
                page,
                componentId,
                displayValue,
                "CY",
                cancellationToken,
                timeoutMs: 4_000);

            // Keyboard navigation is retained only as a last resort after the
            // positive city/country suggestion has already been validated.
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

            if (await MaerskShadowDom.WaitForResolvedLocationValueAsync(
                    page,
                    componentId,
                    searchTerm,
                    displayValue,
                    cancellationToken,
                    timeoutMs: 4_000))
                return;

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

        var trimmed = displayValue.Trim();
        var commaIndex = trimmed.IndexOf(',');
        var cityOnly = commaIndex > 0
            ? trimmed[..commaIndex].Trim()
            : trimmed;

        // Maersk's location field is a human-name typeahead. RKST/port codes
        // such as CHSGH and CRCAL return "No location matching", so never use
        // them as a fallback search term here.
        return new[]
            {
                trimmed,
                cityOnly
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
