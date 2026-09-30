using System.Text.Json;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

internal static class MaerskShadowDom
{
    private const string FillMdsInputScript = """
        args => {
            const normalize = value => (value || '').toString().trim().toLowerCase();
            const names = args.names.map(normalize);

            const matches = host => {
                const description = [
                    host.getAttribute('name'),
                    host.getAttribute('id'),
                    host.getAttribute('label'),
                    host.getAttribute('placeholder'),
                    host.getAttribute('aria-label'),
                    host.getAttribute('type')
                ].map(normalize).join(' ');

                return names.some(name => description.includes(name));
            };

            const setNativeInput = input => {
                input.focus();
                const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
                descriptor?.set?.call(input, args.value);
                input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
                input.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
                return true;
            };

            const find = root => {
                for (const host of root.querySelectorAll('mc-input')) {
                    if (!matches(host)) continue;

                    const nativeInput = host.shadowRoot?.querySelector('input');
                    if (nativeInput) return setNativeInput(nativeInput);

                    if ('value' in host) {
                        host.focus?.();
                        host.value = args.value;
                        host.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
                        host.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
                        return true;
                    }
                }

                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    if (find(host.shadowRoot)) return true;
                }

                return false;
            };

            return find(document);
        }
        """;

    private const string ClickByTextScript = """
        labels => {
            const normalize = value => (value || '').toString().replace(/\s+/g, ' ').trim().toLowerCase();
            const wanted = labels.map(normalize);

            const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && rect.width > 0
                    && rect.height > 0;
            };

            const labelOf = element => normalize(
                element.innerText
                || element.textContent
                || element.getAttribute('aria-label')
                || element.getAttribute('value')
            );

            const find = root => {
                // Prefer the native control inside an open shadow root over the custom host.
                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    const nested = find(host.shadowRoot);
                    if (nested) return nested;
                }

                for (const element of root.querySelectorAll("button,a,input[type='submit'],mc-button,[role='button'],[role='link']")) {
                    if (!isVisible(element)) continue;
                    const label = labelOf(element);
                    if (wanted.some(value => label === value || label.includes(value))) return element;
                }

                return null;
            };

            const target = find(document);
            if (!target) return false;
            target.click();
            return true;
        }
        """;

    private const string WaitForLocationSuggestionScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const compact = value => normalize(value)
                .replace(/[^a-z0-9]+/g, '');

            const rootsOf = node => {
                const roots = [];
                const seen = new Set();

                const visit = root => {
                    if (!root || seen.has(root)) return;
                    seen.add(root);
                    roots.push(root);

                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (element.shadowRoot)
                            visit(element.shadowRoot);
                    }
                };

                visit(node);
                if (node?.shadowRoot)
                    visit(node.shadowRoot);

                return roots;
            };

            let component = null;
            for (const root of rootsOf(document)) {
                for (const candidate of root.querySelectorAll?.(
                    'mc-c-location-servicemode'
                ) || []) {
                    if ((candidate.getAttribute('id') || '') === args.componentId) {
                        component = candidate;
                        break;
                    }
                }

                if (component)
                    break;
            }

            if (!component)
                return false;

            const text = normalize([
                component.innerText,
                component.textContent,
                component.shadowRoot?.textContent
            ].filter(Boolean).join(' '));

            if (text.includes('no results found')
                || text.includes('no location matching')
                || text.includes('no matching location found')) {
                return false;
            }

            const compactText = compact(text);
            const expected = (args.expectedValues || [])
                .map(compact)
                .filter(Boolean);

            // Prefer an actual label/code match in the component text. If MDS
            // exposes only its accessibility announcement, a positive
            // "suggestions available" state is still enough for keyboard
            // selection because the query is scoped to this single component.
            return expected.some(value => compactText.includes(value))
                || /\b[1-9][0-9]* suggestions? available\b/i.test(text);
        }
        """;

    private const string ResolvedLocationValueScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const compact = value => normalize(value)
                .replace(/[^a-z0-9]+/g, '');

            const inputId = args.componentId === 'origin'
                ? 'mc-input-origin'
                : args.componentId === 'destination'
                    ? 'mc-input-destination'
                    : '';

            if (!inputId)
                return false;

            let input = document.getElementById(inputId);

            if (!input) {
                const visit = root => {
                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (element.id === inputId)
                            return element;

                        if (element.shadowRoot) {
                            const nested = visit(element.shadowRoot);
                            if (nested)
                                return nested;
                        }
                    }

                    return null;
                };

                input = visit(document);
            }

            if (!input)
                return false;

            const current = compact(input.value);
            const rawQuery = compact(args.searchTerm);
            const expected = compact(args.displayValue);

            if (!current)
                return false;

            if (current === expected && current !== rawQuery)
                return true;

            if (current !== rawQuery) {
                const city = compact(
                    (args.displayValue || '').toString().split(',')[0]
                );

                return city.length > 0 && current.includes(city);
            }

            return false;
        }
        """;

    private const string SelectLocationSuggestionScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const compact = value => normalize(value)
                .replace(/[^a-z0-9]+/g, '');

            const expected = (args.expectedValues || [])
                .map(compact)
                .filter(Boolean);

            const rootsOf = node => {
                const roots = [];
                const seen = new Set();

                const visit = root => {
                    if (!root || seen.has(root)) return;
                    seen.add(root);
                    roots.push(root);

                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (element.shadowRoot)
                            visit(element.shadowRoot);
                    }
                };

                visit(node);
                if (node?.shadowRoot)
                    visit(node.shadowRoot);

                return roots;
            };

            const findComponent = root => {
                for (const scope of rootsOf(root)) {
                    for (const component of scope.querySelectorAll?.(
                        'mc-c-location-servicemode'
                    ) || []) {
                        if ((component.getAttribute('id') || '') === args.componentId)
                            return component;
                    }
                }

                return null;
            };

            const component = findComponent(document);
            if (!component)
                return false;

            const labelOf = element => [
                element.innerText,
                element.textContent,
                element.shadowRoot?.textContent,
                element.getAttribute?.('label'),
                element.getAttribute?.('aria-label'),
                element.getAttribute?.('value')
            ].filter(Boolean).join(' ');

            const click = element => {
                try {
                    element.scrollIntoView?.({ block: 'nearest' });
                    element.click?.();
                    element.dispatchEvent?.(new MouseEvent('click', {
                        bubbles: true,
                        composed: true
                    }));
                    return true;
                } catch {
                    return false;
                }
            };

            const candidates = [];
            for (const root of rootsOf(component)) {
                for (const option of root.querySelectorAll?.(
                    "mc-option,[role='option'],li[role='option'],[part*='option'],[data-test*='suggestion' i],[data-testid*='suggestion' i]"
                ) || []) {
                    if (!candidates.includes(option))
                        candidates.push(option);
                }
            }

            for (const option of candidates) {
                const label = compact(labelOf(option));

                if (expected.some(value =>
                    label === value
                    || label.includes(value)
                    || value.includes(label))) {
                    return click(option);
                }
            }

            // The location list is scoped to one origin/destination component, so
            // choosing its first returned option is safer than the old page-global
            // fallback which could hit another typeahead.
            return candidates.length > 0 && click(candidates[0]);
        }
        """;

    private const string IsLocationSelectionSettledScript = """
        componentId => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const rootsOf = node => {
                const roots = [];
                const seen = new Set();

                const visit = root => {
                    if (!root || seen.has(root)) return;
                    seen.add(root);
                    roots.push(root);

                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (element.shadowRoot)
                            visit(element.shadowRoot);
                    }
                };

                visit(node);
                if (node?.shadowRoot)
                    visit(node.shadowRoot);

                return roots;
            };

            let component = null;
            for (const root of rootsOf(document)) {
                for (const candidate of root.querySelectorAll?.(
                    'mc-c-location-servicemode'
                ) || []) {
                    if ((candidate.getAttribute('id') || '') === componentId) {
                        component = candidate;
                        break;
                    }
                }

                if (component)
                    break;
            }

            if (!component)
                return false;

            const text = normalize([
                component.innerText,
                component.textContent,
                component.shadowRoot?.textContent
            ].filter(Boolean).join(' '));

            if (text.includes('cannot be left blank')
                || text.includes('no location matching')
                || text.includes('no matching location found')) {
                return false;
            }

            // When the list is still open Maersk announces the available
            // suggestions through its live region. A committed selection closes
            // that state and leaves the CY/SD badge.
            if (text.includes('suggestions available')
                || text.includes('first:')) {
                return false;
            }

            const serviceMode = normalize(component.getAttribute('servicemode'));
            return serviceMode === 'cy' || serviceMode === 'sd';
        }
        """;

    private const string SelectContainerYardServiceModesScript = """
        () => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const rootsOf = node => {
                const roots = [];
                const seen = new Set();

                const visit = root => {
                    if (!root || seen.has(root)) return;
                    seen.add(root);
                    roots.push(root);

                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (element.shadowRoot)
                            visit(element.shadowRoot);
                    }
                };

                visit(node);
                if (node?.shadowRoot)
                    visit(node.shadowRoot);

                return roots;
            };

            const elementText = element => normalize([
                element.innerText,
                element.textContent,
                element.getAttribute?.('label'),
                element.getAttribute?.('aria-label'),
                element.getAttribute?.('value'),
                element.getAttribute?.('data-value'),
                element.getAttribute?.('data-cy'),
                element.getAttribute?.('name')
            ].filter(Boolean).join(' '));

            const isCy = element => {
                const text = elementText(element);
                return text === 'cy'
                    || text.includes('container yard')
                    || text.includes('container-yard')
                    || text.includes('containeryard')
                    || text.includes('service mode cy')
                    || text.includes('servicemode cy');
            };

            const interactiveAncestor = element => {
                let current = element;

                while (current && current instanceof Element) {
                    const tag = current.tagName?.toLowerCase?.() || '';
                    const role = normalize(current.getAttribute?.('role'));

                    if (['button', 'label', 'input', 'option', 'mc-radio', 'mc-button', 'mc-option', 'mc-tab'].includes(tag)
                        || ['radio', 'button', 'option', 'tab'].includes(role)
                        || current.hasAttribute?.('tabindex')) {
                        return current;
                    }

                    current = current.parentElement;
                }

                return element;
            };

            const activate = element => {
                const target = interactiveAncestor(element);
                const input =
                    target instanceof HTMLInputElement
                        ? target
                        : target.shadowRoot?.querySelector?.("input[type='radio'],input[type='checkbox']")
                          || target.querySelector?.("input[type='radio'],input[type='checkbox']");

                try {
                    if (input) {
                        if (input.checked)
                            return true;

                        input.click();

                        if (!input.checked) {
                            const descriptor = Object.getOwnPropertyDescriptor(
                                HTMLInputElement.prototype,
                                'checked');

                            descriptor?.set?.call(input, true);
                            input.dispatchEvent(new Event('input', {
                                bubbles: true,
                                composed: true
                            }));
                            input.dispatchEvent(new Event('change', {
                                bubbles: true,
                                composed: true
                            }));
                        }

                        return !!input.checked;
                    }

                    target.click();
                    return true;
                } catch {
                    return false;
                }
            };

            const allRoots = rootsOf(document);
            const components = [];

            for (const root of allRoots) {
                for (const component of root.querySelectorAll?.('mc-c-location-servicemode') || []) {
                    if (!components.includes(component))
                        components.push(component);
                }
            }

            let activated = 0;

            const activateCyInside = component => {
                for (const root of rootsOf(component)) {
                    // First prefer semantic/interactive controls.
                    for (const element of root.querySelectorAll?.(
                        "input,button,label,mc-radio,mc-button,mc-option,[role='radio'],[role='button'],[role='option'],[tabindex]"
                    ) || []) {
                        if (isCy(element) && activate(element))
                            return true;
                    }

                    // Maersk has also rendered the label as a plain span/div inside a
                    // clickable parent. Inspect all descendants and climb to the
                    // closest interactive ancestor.
                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (isCy(element) && activate(element))
                            return true;
                    }
                }

                return false;
            };

            if (components.length > 0) {
                for (const component of components) {
                    if (activateCyInside(component))
                        activated++;
                }

                // Some versions render one component that controls both ends.
                return activated === components.length || (components.length === 1 && activated === 1);
            }

            // Fallback when the custom host is flattened/renamed but the visible
            // "Container Yard" controls are still present.
            for (const root of allRoots) {
                for (const element of root.querySelectorAll?.('*') || []) {
                    if (isCy(element) && activate(element))
                        activated++;
                }
            }

            return activated > 0;
        }
        """;

    private const string SelectPriceOwnerScript = """
        () => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && rect.width > 0
                    && rect.height > 0;
            };

            const dispatchChecked = input => {
                if (!input) return false;

                try {
                    input.click();
                } catch {
                    // Fall through to native property/event assignment.
                }

                if (!input.checked) {
                    const descriptor = Object.getOwnPropertyDescriptor(
                        HTMLInputElement.prototype,
                        'checked');

                    descriptor?.set?.call(input, true);
                    input.dispatchEvent(new Event('input', {
                        bubbles: true,
                        composed: true
                    }));
                    input.dispatchEvent(new Event('change', {
                        bubbles: true,
                        composed: true
                    }));
                }

                return !!input.checked;
            };

            const findNativeRadio = root => {
                const radios = Array.from(
                    root.querySelectorAll("input[type='radio'][name='priceOwner']")
                );

                if (radios.length > 0) {
                    const checked = radios.find(x => x.checked);
                    if (checked && radios.indexOf(checked) === 0)
                        return true;

                    return dispatchChecked(radios[0]);
                }

                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    const nested = findNativeRadio(host.shadowRoot);
                    if (nested) return true;
                }

                return false;
            };

            const selectByLabel = root => {
                for (const radio of root.querySelectorAll('mc-radio')) {
                    const text = normalize(
                        radio.innerText
                        || radio.textContent
                        || radio.getAttribute('label')
                        || radio.getAttribute('aria-label')
                        || radio.getAttribute('value'));

                    if (!text.includes('i am the price owner'))
                        continue;

                    const nativeInput = radio.shadowRoot?.querySelector(
                        "input[type='radio']");

                    if (nativeInput && dispatchChecked(nativeInput))
                        return true;

                    try {
                        radio.click();
                        return true;
                    } catch {
                        // Continue looking for a native input fallback.
                    }
                }

                for (const element of root.querySelectorAll('label,[role="radio"]')) {
                    if (!isVisible(element)) continue;
                    if (!normalize(element.innerText || element.textContent)
                        .includes('i am the price owner'))
                        continue;

                    try {
                        element.click();
                        return true;
                    } catch {
                        // Continue to fallback.
                    }
                }

                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    if (selectByLabel(host.shadowRoot)) return true;
                }

                return false;
            };

            selectByLabel(document);

            // Verify/force the first priceOwner radio. In Maersk's current booking
            // form the first radio is "I am the price owner".
            return findNativeRadio(document);
        }
        """;

    private const string DismissCoachmarksScript = """
        () => {
            let dismissed = false;

            const visit = root => {
                for (const popover of root.querySelectorAll('mc-popover.location-coachmark-popover[open], .location-coachmark-popover[open]')) {
                    try {
                        popover.removeAttribute('open');
                        popover.setAttribute('aria-hidden', 'true');
                        popover.style.pointerEvents = 'none';
                        popover.style.display = 'none';
                        dismissed = true;
                    } catch {
                        // Keep inspecting other roots.
                    }
                }

                for (const host of root.querySelectorAll('*')) {
                    if (host.shadowRoot) visit(host.shadowRoot);
                }
            };

            visit(document);
            return dismissed;
        }
        """;

    private const string DescribeScript = """
        () => {
            const result = {
                title: document.title || '',
                bodyText: (document.body?.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 700),
                mdsInputs: [],
                inputs: [],
                customElements: [],
                visibleOptions: [],
                serviceModes: []
            };

            const visit = root => {
                for (const input of root.querySelectorAll('input')) {
                    const style = getComputedStyle(input);
                    const rect = input.getBoundingClientRect();
                    result.inputs.push({
                        type: input.getAttribute('type') || '',
                        name: input.getAttribute('name') || '',
                        id: input.getAttribute('id') || '',
                        autocomplete: input.getAttribute('autocomplete') || '',
                        placeholder: input.getAttribute('placeholder') || '',
                        ariaLabel: input.getAttribute('aria-label') || '',
                        value: input.value || '',
                        disabled: !!input.disabled,
                        readOnly: !!input.readOnly,
                        checked: !!input.checked,
                        visible: style.display !== 'none'
                            && style.visibility !== 'hidden'
                            && rect.width > 0
                            && rect.height > 0
                    });
                }

                for (const component of root.querySelectorAll('mc-c-location-servicemode')) {
                    const attrs = {};
                    for (const attr of component.attributes || [])
                        attrs[attr.name] = attr.value;

                    result.serviceModes.push({
                        text: (component.innerText || component.textContent || '')
                            .replace(/\s+/g, ' ')
                            .trim()
                            .slice(0, 500),
                        attributes: attrs,
                        hasShadowRoot: !!component.shadowRoot,
                        shadowText: (component.shadowRoot?.textContent || '')
                            .replace(/\s+/g, ' ')
                            .trim()
                            .slice(0, 500)
                    });
                }

                for (const option of root.querySelectorAll("mc-option,[role='option'],li[role='option']")) {
                    const style = getComputedStyle(option);
                    const rect = option.getBoundingClientRect();
                    if (style.display !== 'none'
                        && style.visibility !== 'hidden'
                        && rect.width > 0
                        && rect.height > 0) {
                        result.visibleOptions.push({
                            text: (option.innerText || option.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 250),
                            value: option.getAttribute('value') || ''
                        });
                    }
                }

                for (const host of root.querySelectorAll('*')) {
                    if (host.tagName.includes('-') && !result.customElements.includes(host.tagName.toLowerCase()))
                        result.customElements.push(host.tagName.toLowerCase());
                }

                for (const host of root.querySelectorAll('mc-input')) {
                    const nativeInput = host.shadowRoot?.querySelector('input');
                    result.mdsInputs.push({
                        name: host.getAttribute('name') || '',
                        id: host.getAttribute('id') || '',
                        label: host.getAttribute('label') || '',
                        placeholder: host.getAttribute('placeholder') || '',
                        ariaLabel: host.getAttribute('aria-label') || '',
                        type: host.getAttribute('type') || '',
                        hasShadowRoot: !!host.shadowRoot,
                        hasNativeInput: !!nativeInput,
                        nativeType: nativeInput?.getAttribute('type') || '',
                        nativeName: nativeInput?.getAttribute('name') || ''
                    });
                }

                for (const host of root.querySelectorAll('*')) {
                    if (host.shadowRoot) visit(host.shadowRoot);
                }
            };

            visit(document);
            return result;
        }
        """;

    private static async Task<bool> TryActOnVisibleLocatorAsync(
        IFrame frame,
        IReadOnlyCollection<string> selectors,
        Func<ILocator, Task> action)
    {
        foreach (var selector in selectors)
        {
            ILocator matches;
            int count;

            try
            {
                // Playwright CSS locators pierce open shadow roots and are resilient
                // to DOM replacement because the element is resolved again per action.
                matches = frame.Locator(selector);
                count = await matches.CountAsync();
            }
            catch (PlaywrightException)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var candidate = matches.Nth(index);

                try
                {
                    if (!await candidate.IsVisibleAsync())
                        continue;

                    await action(candidate);
                    return true;
                }
                catch (PlaywrightException)
                {
                    // The Maersk SPA can replace controls between visibility checks and
                    // actions. A Locator re-resolves on the next candidate/iteration.
                }
            }
        }

        return false;
    }

    private static async Task<bool> TryFillOnVisibleEditableLocatorAsync(
        IFrame frame,
        IReadOnlyCollection<string> selectors,
        string value,
        int actionTimeoutMs)
    {
        foreach (var selector in selectors)
        {
            ILocator matches;
            int count;

            try
            {
                matches = frame.Locator(selector);
                count = await matches.CountAsync();
            }
            catch (PlaywrightException)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var candidate = matches.Nth(index);

                try
                {
                    if (!await candidate.IsVisibleAsync()
                        || !await candidate.IsEnabledAsync()
                        || !await candidate.IsEditableAsync())
                        continue;

                    await candidate.FillAsync(
                        value,
                        new LocatorFillOptions
                        {
                            Timeout = actionTimeoutMs
                        });
                    return true;
                }
                catch (PlaywrightException)
                {
                    // The SPA can change enabled/editable state between checks.
                    // Re-resolve on the next polling pass.
                }
            }
        }

        return false;
    }

    public static async Task<bool> TypeAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        string value,
        CancellationToken cancellationToken,
        int timeoutMs = 10_000,
        int delayMs = 50)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                foreach (var selector in selectors)
                {
                    ILocator matches;
                    int count;

                    try
                    {
                        matches = frame.Locator(selector);
                        count = await matches.CountAsync();
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var candidate = matches.Nth(index);

                        try
                        {
                            if (!await candidate.IsVisibleAsync()
                                || !await candidate.IsEnabledAsync()
                                || !await candidate.IsEditableAsync())
                                continue;

                            await candidate.FillAsync(
                                string.Empty,
                                new LocatorFillOptions { Timeout = 1_500 });

                            await candidate.PressSequentiallyAsync(
                                value,
                                new LocatorPressSequentiallyOptions
                                {
                                    Delay = delayMs,
                                    Timeout = Math.Min(5_000, timeoutMs)
                                });

                            return true;
                        }
                        catch (PlaywrightException)
                        {
                            // Retry because the SPA can replace the native input
                            // while the custom element re-renders.
                        }
                    }
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> HasVisibleAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        CancellationToken cancellationToken)
    {
        foreach (var frame in page.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryActOnVisibleLocatorAsync(
                    frame,
                    selectors,
                    _ => Task.CompletedTask))
                return true;
        }

        return false;
    }

    public static async Task<bool> FillAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        string value,
        CancellationToken cancellationToken,
        int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                var remainingMs = Math.Max(
                    250,
                    (int)(deadline - DateTime.UtcNow).TotalMilliseconds);

                if (await TryFillOnVisibleEditableLocatorAsync(
                        frame,
                        selectors,
                        value,
                        Math.Min(remainingMs, 1_500)))
                    return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> FillMdsInputAsync(
        IPage page,
        IReadOnlyCollection<string> semanticNames,
        string value,
        CancellationToken cancellationToken)
    {
        foreach (var frame in page.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await frame.EvaluateAsync<bool>(
                        FillMdsInputScript,
                        new { names = semanticNames.ToArray(), value }))
                    return true;
            }
            catch (PlaywrightException)
            {
                // Try the next frame.
            }
        }

        return false;
    }

    public static async Task<bool> PressFirstAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        string key,
        CancellationToken cancellationToken,
        int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                if (await TryActOnVisibleLocatorAsync(
                        frame,
                        selectors,
                        locator => locator.PressAsync(
                            key,
                            new LocatorPressOptions { Timeout = 5_000 })))
                    return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> ClickVisibleOptionMatchingAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        IReadOnlyCollection<string> expectedValues,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000,
        bool exactOnly = false)
    {
        static string Normalize(string? value)
            => new((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        var expected = expectedValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (expected.Length == 0)
            return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                foreach (var selector in selectors)
                {
                    ILocator matches;
                    int count;

                    try
                    {
                        matches = frame.Locator(selector);
                        count = await matches.CountAsync();
                    }
                    catch (PlaywrightException)
                    {
                        continue;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var candidate = matches.Nth(index);

                        try
                        {
                            if (!await candidate.IsVisibleAsync()
                                || !await candidate.IsEnabledAsync())
                                continue;

                            var text = (await candidate.InnerTextAsync()).Trim();
                            var value = await candidate.GetAttributeAsync("value");
                            var normalizedText = Normalize(text);
                            var normalizedValue = Normalize(value);

                            var isMatch = exactOnly
                                ? expected.Any(x =>
                                    string.Equals(normalizedText, x, StringComparison.Ordinal)
                                    || string.Equals(normalizedValue, x, StringComparison.Ordinal))
                                : expected.Any(x =>
                                    normalizedText.Contains(x, StringComparison.Ordinal)
                                    || normalizedValue.Contains(x, StringComparison.Ordinal));

                            if (!isMatch)
                                continue;

                            try
                            {
                                await candidate.ClickAsync(
                                    new LocatorClickOptions { Timeout = 1_500 });
                            }
                            catch (PlaywrightException)
                            {
                                await candidate.ClickAsync(
                                    new LocatorClickOptions
                                    {
                                        Timeout = 1_500,
                                        Force = true
                                    });
                            }

                            return true;
                        }
                        catch (PlaywrightException)
                        {
                            // Continue with the next option while the SPA settles.
                        }
                    }
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> ClickFirstAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        CancellationToken cancellationToken,
        int timeoutMs = 10_000,
        bool force = false)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                if (await TryActOnVisibleLocatorAsync(
                        frame,
                        selectors,
                        locator => locator.ClickAsync(
                            new LocatorClickOptions
                            {
                                Timeout = Math.Min(timeoutMs, 5_000),
                                Force = force
                            })))
                    return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> WaitForLocationSuggestionAsync(
        IPage page,
        string componentId,
        IReadOnlyCollection<string> expectedValues,
        CancellationToken cancellationToken,
        int timeoutMs = 6_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(
                            WaitForLocationSuggestionScript,
                            new
                            {
                                componentId,
                                expectedValues = expectedValues.ToArray()
                            }))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while the remote typeahead is loading/re-rendering.
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> WaitForResolvedLocationValueAsync(
        IPage page,
        string componentId,
        string searchTerm,
        string displayValue,
        CancellationToken cancellationToken,
        int timeoutMs = 3_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(
                            ResolvedLocationValueScript,
                            new
                            {
                                componentId,
                                searchTerm,
                                displayValue
                            }))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while the selected value is committed by MDS.
                }
            }

            await Task.Delay(150, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SelectLocationSuggestionAsync(
        IPage page,
        string componentId,
        IReadOnlyCollection<string> expectedValues,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(
                            SelectLocationSuggestionScript,
                            new
                            {
                                componentId,
                                expectedValues = expectedValues.ToArray()
                            }))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while Maersk refreshes the remote location list.
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> IsLocationSelectionSettledAsync(
        IPage page,
        string componentId,
        CancellationToken cancellationToken,
        int timeoutMs = 3_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(
                            IsLocationSelectionSettledScript,
                            componentId))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while the typeahead commits its selection.
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SelectContainerYardServiceModesAsync(
        IPage page,
        CancellationToken cancellationToken,
        int timeoutMs = 8_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(
                            SelectContainerYardServiceModesScript))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while Maersk renders/replaces the CY/CY controls.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SelectPriceOwnerAsync(
        IPage page,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(SelectPriceOwnerScript))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while Maersk renders/replaces the price-owner controls.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> DismissBlockingCoachmarksAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var dismissed = false;

        foreach (var frame in page.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                dismissed |= await frame.EvaluateAsync<bool>(DismissCoachmarksScript);
            }
            catch (PlaywrightException)
            {
                // Continue with remaining frames. The booking SPA can replace
                // a frame while the coachmark is being dismissed.
            }
        }

        return dismissed;
    }

    public static async Task<bool> ClickByTextAsync(
        IPage page,
        IReadOnlyCollection<string> labels,
        CancellationToken cancellationToken,
        int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.EvaluateAsync<bool>(ClickByTextScript, labels.ToArray()))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Try again while the SPA settles.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SelectOptionByLabelAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        string label,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                if (await TryActOnVisibleLocatorAsync(
                        frame,
                        selectors,
                        locator => locator.SelectOptionAsync(
                            new[] { new SelectOptionValue { Label = label } },
                            new LocatorSelectOptionOptions { Timeout = 5_000 })))
                    return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static async Task<string?> ReadVisibleAuthenticationMessageAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var selectors = new[]
        {
            "mc-error",
            "[role='alert']",
            "[aria-live='assertive']",
            "[aria-live='polite']",
            ".error",
            ".error-message",
            "[class*='error' i]"
        };

        foreach (var frame in page.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var selector in selectors)
            {
                try
                {
                    var matches = frame.Locator(selector);
                    var count = await matches.CountAsync();

                    for (var index = 0; index < count; index++)
                    {
                        var candidate = matches.Nth(index);
                        if (!await candidate.IsVisibleAsync())
                            continue;

                        var text = (await candidate.InnerTextAsync()).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            return text.Length <= 1000 ? text : text[..1000];
                    }
                }
                catch (PlaywrightException)
                {
                    // Continue inspecting other selectors/frames.
                }
            }
        }

        return null;
    }

    public static async Task<string> DescribeAsync(IPage page)
    {
        var frames = new List<object>();

        foreach (var frame in page.Frames)
        {
            try
            {
                var description = await frame.EvaluateAsync<JsonElement>(DescribeScript);
                frames.Add(new
                {
                    url = frame.Url,
                    description
                });
            }
            catch (PlaywrightException)
            {
                frames.Add(new
                {
                    url = frame.Url,
                    error = "Unable to inspect frame."
                });
            }
        }

        return JsonSerializer.Serialize(frames);
    }
}
