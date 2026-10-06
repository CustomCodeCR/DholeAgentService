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

            const isEnabled = element => {
                if (element.hasAttribute?.('disabled'))
                    return false;

                if ((element.getAttribute?.('aria-disabled') || '').toLowerCase() === 'true')
                    return false;

                const native = element.shadowRoot?.querySelector?.(
                    "button,input[type='submit'],input[type='button']"
                );

                if (native?.disabled)
                    return false;

                return true;
            };

            const find = root => {
                // Prefer the native control inside an open shadow root over the custom host.
                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    const nested = find(host.shadowRoot);
                    if (nested) return nested;
                }

                for (const element of root.querySelectorAll("button,a,input[type='submit'],mc-button,[role='button'],[role='link']")) {
                    if (!isVisible(element) || !isEnabled(element)) continue;
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

    private const string SelectTypeaheadOptionScript = """
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

            if (expected.length === 0)
                return false;

            const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && rect.width > 0
                    && rect.height > 0;
            };

            const isEnabled = element =>
                !element.hasAttribute?.('disabled')
                && element.getAttribute?.('aria-disabled') !== 'true';

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

            visit(document);

            const candidates = [];
            for (const root of roots) {
                for (const option of root.querySelectorAll?.(
                    "mc-option,[role='option'],li[role='option']"
                ) || []) {
                    if (!candidates.includes(option))
                        candidates.push(option);
                }
            }

            for (const option of candidates) {
                if (!isVisible(option) || !isEnabled(option))
                    continue;

                const text = compact(
                    option.innerText
                    || option.textContent
                    || option.getAttribute?.('label')
                );
                const value = compact(option.getAttribute?.('value'));

                const matches = args.exactOnly
                    ? expected.some(x => text === x || value === x)
                    : expected.some(x =>
                        text.includes(x)
                        || value.includes(x)
                        || x.includes(text)
                        || x.includes(value));

                if (!matches)
                    continue;

                try {
                    option.scrollIntoView?.({ block: 'center', inline: 'nearest' });
                    option.click?.();
                    option.dispatchEvent?.(new MouseEvent('click', {
                        bubbles: true,
                        composed: true
                    }));
                    return true;
                } catch {
                    // Continue with another matching representation.
                }
            }

            return false;
        }
        """;

    private const string SelectContainerTypeScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/[^a-z0-9]+/gi, '')
                .toUpperCase();

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

            const components = [];
            for (const root of rootsOf(document)) {
                for (const component of root.querySelectorAll?.(
                    'mc-c-container-select,mc-c-container-selection-input'
                ) || []) {
                    if (!components.includes(component))
                        components.push(component);
                }
            }

            const expectedLabel = normalize(args.label);
            const expectedCode = normalize(args.code);
            const candidates = [];

            for (const component of components) {
                for (const root of rootsOf(component)) {
                    for (const element of root.querySelectorAll?.('*') || []) {
                        const text = (element.innerText || element.textContent || '').trim();
                        const normalized = normalize(text);
                        const value = normalize(
                            element.getAttribute?.('value')
                            || element.getAttribute?.('data-value')
                            || element.value
                        );

                        const codeMatch = expectedCode
                            && value === expectedCode;
                        const labelMatch = normalized
                            && (normalized === expectedLabel
                                || (normalized.startsWith(expectedLabel)
                                    && normalized.length <= expectedLabel.length + 40));

                        if (codeMatch || labelMatch) {
                            candidates.push({
                                element,
                                textLength: text.length,
                                priority: codeMatch ? 0 : 1
                            });
                        }
                    }
                }
            }

            candidates.sort((a, b) =>
                a.priority - b.priority
                || a.textLength - b.textLength);

            const interactive = element => {
                let current = element;

                while (current && current instanceof Element) {
                    const tag = current.tagName?.toLowerCase?.() || '';
                    const role = (current.getAttribute?.('role') || '').toLowerCase();

                    if (['button', 'li', 'mc-option', 'mc-button'].includes(tag)
                        || ['option', 'button'].includes(role)
                        || current.hasAttribute?.('tabindex')) {
                        return current;
                    }

                    current = current.parentElement;
                }

                return element;
            };

            for (const candidate of candidates) {
                try {
                    const target = interactive(candidate.element);
                    target.scrollIntoView?.({ block: 'nearest' });
                    target.click?.();
                    target.dispatchEvent?.(new MouseEvent('click', {
                        bubbles: true,
                        composed: true
                    }));
                    return true;
                } catch {
                    // Try the next matching representation.
                }
            }

            return false;
        }
        """;

    private const string SetContainerQuantityScript = """
        args => {
            const desired = Number(args.quantity);
            if (!Number.isFinite(desired) || desired < 1)
                return false;

            const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && rect.width > 0
                    && rect.height > 0;
            };

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

            let visibleInput = null;
            let stepper = null;

            for (const root of rootsOf(document)) {
                for (const input of root.querySelectorAll?.("input[name='containers']") || []) {
                    if (isVisible(input)) {
                        visibleInput = input;
                        break;
                    }
                }

                if (visibleInput)
                    break;
            }

            if (!visibleInput)
                return false;

            const current = Number(visibleInput.value);
            if (current === desired)
                return !visibleInput.disabled;

            let node = visibleInput;
            while (node && node instanceof Element) {
                if (node.tagName?.toLowerCase?.() === 'mc-number-stepper') {
                    stepper = node;
                    break;
                }
                node = node.parentElement;
            }

            if (!stepper) {
                for (const root of rootsOf(document)) {
                    const candidate = root.querySelector?.('mc-number-stepper');
                    if (candidate) {
                        stepper = candidate;
                        break;
                    }
                }
            }

            if (!stepper)
                return false;

            const buttons = [];
            for (const root of rootsOf(stepper)) {
                for (const button of root.querySelectorAll?.(
                    "button,mc-button,[role='button']"
                ) || []) {
                    if (isVisible(button) && !buttons.includes(button))
                        buttons.push(button);
                }
            }

            if (buttons.length < 2)
                return false;

            let value = Number(visibleInput.value);
            let guard = 0;

            while (value !== desired && guard++ < 50) {
                const button = desired > value
                    ? buttons[buttons.length - 1]
                    : buttons[0];

                button.click();
                value = Number(visibleInput.value);
            }

            return Number(visibleInput.value) === desired;
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

            const expectedTokenSets = (args.expectedValues || [])
                .map(value => normalize(value)
                    .split(/[^a-z0-9]+/g)
                    .filter(token => token.length > 1))
                .filter(items => items.length > 0);

            const hasExpectedLocation = expectedTokenSets.some(expected =>
                expected.every(token => text.includes(token)));

            // Maersk can leave stale "No matching location found" text in the
            // live region at the same time it announces a valid result. Trust
            // the positive suggestion announcement, not the stale error text.
            // Requiring both a positive count and all city/country tokens keeps
            // "Puerto Caldera, Costa Rica" distinct from "Caldera, Chile".
            const suggestionMatch = text.match(/\b([1-9]\d*)\s+suggestions?\s+available\b/);
            const hasPositiveSuggestion = suggestionMatch
                && Number(suggestionMatch[1]) > 0;

            return !!hasPositiveSuggestion && hasExpectedLocation;
        }
        """;

    private const string ResolvedLocationValueScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const tokens = value => normalize(value)
                .split(/[^a-z0-9]+/g)
                .filter(token => token.length > 1);

            const distance = (left, right) => {
                if (left === right) return 0;
                if (!left) return right.length;
                if (!right) return left.length;

                const previous = Array.from(
                    { length: right.length + 1 },
                    (_, index) => index);

                for (let i = 1; i <= left.length; i++) {
                    const current = [i];

                    for (let j = 1; j <= right.length; j++) {
                        const cost = left[i - 1] === right[j - 1] ? 0 : 1;
                        current[j] = Math.min(
                            current[j - 1] + 1,
                            previous[j] + 1,
                            previous[j - 1] + cost);
                    }

                    for (let j = 0; j < current.length; j++)
                        previous[j] = current[j];
                }

                return previous[right.length];
            };

            const threshold = token =>
                token.length <= 4 ? 0 : token.length <= 8 ? 1 : 2;

            const fuzzyTokensMatch = (expected, actual) => {
                if (expected.length === 0 || actual.length === 0)
                    return false;

                return expected.every(expectedToken => {
                    if (actual.includes(expectedToken))
                        return true;

                    let best = Number.MAX_SAFE_INTEGER;
                    for (const actualToken of actual)
                        best = Math.min(best, distance(expectedToken, actualToken));

                    return best <= threshold(expectedToken);
                });
            };

            const inputId = args.componentId === 'origin'
                ? 'mc-input-origin'
                : args.componentId === 'destination'
                    ? 'mc-input-destination'
                    : '';

            if (!inputId)
                return false;

            const roots = [];
            const seen = new Set();

            const collectRoots = root => {
                if (!root || seen.has(root))
                    return;

                seen.add(root);
                roots.push(root);

                for (const element of root.querySelectorAll?.('*') || []) {
                    if (element.shadowRoot)
                        collectRoots(element.shadowRoot);
                }
            };

            collectRoots(document);

            let input = document.getElementById(inputId);
            if (!input) {
                for (const root of roots) {
                    const candidate = root.querySelector?.('#' + inputId);
                    if (candidate) {
                        input = candidate;
                        break;
                    }
                }
            }

            if (!input || !normalize(input.value))
                return false;

            if (!fuzzyTokensMatch(
                    tokens(args.displayValue),
                    tokens(input.value)))
                return false;

            let component = null;
            for (const root of roots) {
                for (const element of root.querySelectorAll?.(
                    'mc-c-location-servicemode'
                ) || []) {
                    if ((element.getAttribute('id') || '') === args.componentId) {
                        component = element;
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
                || text.includes('no matching location found')
                || text.includes('suggestions available')
                || text.includes('first:')) {
                return false;
            }

            // We only search ocean CY/CY rates. A typed value, a stale label, or
            // a Store Door selection must never be accepted as committed.
            const serviceMode = normalize(component.getAttribute('servicemode'));
            if (serviceMode !== 'cy')
                return false;

            const isVisible = element => {
                if (!(element instanceof Element))
                    return false;

                const style = getComputedStyle(element);
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && style.opacity !== '0'
                    && element.getClientRects().length > 0;
            };

            for (const root of roots) {
                for (const option of root.querySelectorAll?.('mc-option') || []) {
                    if (isVisible(option))
                        return false;
                }
            }

            return true;
        }
        """;

    private const string SelectLocationSuggestionScript = """
        args => {
            const normalize = value => (value || '')
                .toString()
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();

            const tokens = value => normalize(value)
                .split(/[^a-z0-9]+/g)
                .filter(token => token.length > 1);

            const expectedTokenSets = (args.expectedValues || [])
                .map(tokens)
                .filter(items => items.length > 0);

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

            const labelOf = element => normalize([
                element.innerText,
                element.textContent,
                element.shadowRoot?.textContent,
                element.getAttribute?.('label'),
                element.getAttribute?.('aria-label'),
                element.getAttribute?.('value')
            ].filter(Boolean).join(' '));

            const matches = element => {
                const label = labelOf(element);
                if (!label)
                    return false;

                return expectedTokenSets.some(expected =>
                    expected.every(token => label.includes(token)));
            };

            const interactive = element => {
                let current = element;

                while (current && current instanceof Element) {
                    const tag = current.tagName?.toLowerCase?.() || '';
                    const role = normalize(current.getAttribute?.('role'));

                    if (['button', 'li', 'mc-option', 'mc-button'].includes(tag)
                        || ['option', 'button'].includes(role)
                        || current.hasAttribute?.('tabindex')) {
                        return current;
                    }

                    current = current.parentElement;
                }

                return element;
            };

            const click = element => {
                try {
                    const target = interactive(element);
                    target.scrollIntoView?.({ block: 'nearest' });
                    target.click?.();
                    target.dispatchEvent?.(new MouseEvent('click', {
                        bubbles: true,
                        composed: true
                    }));
                    return true;
                } catch {
                    return false;
                }
            };

            const matchesFound = [];

            for (const root of rootsOf(component)) {
                for (const option of root.querySelectorAll?.(
                    "mc-option,[role='option'],li[role='option'],[part*='option'],[data-test*='suggestion' i],[data-testid*='suggestion' i]"
                ) || []) {
                    if (matches(option)) {
                        matchesFound.push({
                            element: option,
                            length: labelOf(option).length
                        });
                    }
                }
            }

            if (matchesFound.length === 0) {
                for (const root of rootsOf(component)) {
                    for (const element of root.querySelectorAll?.('*') || []) {
                        if (matches(element)) {
                            matchesFound.push({
                                element,
                                length: labelOf(element).length
                            });
                        }
                    }
                }
            }

            matchesFound.sort((a, b) => a.length - b.length);

            for (const match of matchesFound) {
                if (click(match.element))
                    return true;
            }

            return false;
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

    public static async Task<bool> ClickVisibleLocationOptionAsync(
        IPage page,
        string componentId,
        string displayValue,
        string serviceMode,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000)
    {
        static string[] Tokens(string? value)
            => (value ?? string.Empty)
                .ToUpperInvariant()
                .Split(
                    [
                        ' ', ',', '.', '(', ')', '-', '_', '/', '\\', ':', ';'
                    ],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 1)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        static int Distance(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal))
                return 0;

            if (left.Length == 0)
                return right.Length;

            if (right.Length == 0)
                return left.Length;

            var previous = Enumerable.Range(0, right.Length + 1).ToArray();

            for (var i = 1; i <= left.Length; i++)
            {
                var current = new int[right.Length + 1];
                current[0] = i;

                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(
                            current[j - 1] + 1,
                            previous[j] + 1),
                        previous[j - 1] + cost);
                }

                previous = current;
            }

            return previous[right.Length];
        }

        static int Threshold(string token)
            => token.Length <= 4
                ? 0
                : token.Length <= 8
                    ? 1
                    : 2;

        static int MatchScore(
            IReadOnlyCollection<string> expected,
            IReadOnlyCollection<string> actual)
        {
            if (expected.Count == 0 || actual.Count == 0)
                return -1;

            var totalDistance = 0;

            foreach (var expectedToken in expected)
            {
                if (actual.Contains(expectedToken, StringComparer.Ordinal))
                    continue;

                var best = actual.Min(actualToken =>
                    Distance(expectedToken, actualToken));

                if (best > Threshold(expectedToken))
                    return -1;

                totalDistance += best;
            }

            return 100 - (totalDistance * 10);
        }

        var expectedTokens = Tokens(displayValue);
        if (expectedTokens.Length == 0)
            return false;

        var desiredMode = serviceMode.Trim().ToUpperInvariant();
        var desiredSuffix = desiredMode.Length == 0
            ? string.Empty
            : $"-{desiredMode}";

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                ILocator matches;
                int count;

                try
                {
                    matches = frame.Locator(
                        $"mc-c-location-servicemode#{componentId} mc-option");

                    count = await matches.CountAsync();

                    if (count == 0)
                    {
                        matches = frame.Locator("mc-option");
                        count = await matches.CountAsync();
                    }
                }
                catch (PlaywrightException)
                {
                    continue;
                }

                var candidates = new List<(ILocator Locator, int Score, int LabelLength)>();

                for (var index = 0; index < count; index++)
                {
                    var candidate = matches.Nth(index);

                    try
                    {
                        if (!await candidate.IsVisibleAsync(
                                new LocatorIsVisibleOptions { Timeout = 750 })
                            || !await candidate.IsEnabledAsync(
                                new LocatorIsEnabledOptions { Timeout = 750 }))
                            continue;

                        var text = (await candidate.InnerTextAsync(
                            new LocatorInnerTextOptions { Timeout = 750 })).Trim();

                        var value = (await candidate.GetAttributeAsync(
                            "value",
                            new LocatorGetAttributeOptions { Timeout = 750 }) ?? string.Empty).Trim();

                        var normalizedText = text.ToUpperInvariant();
                        var normalizedValue = value.ToUpperInvariant();

                        var isDesiredMode = desiredMode.Length == 0
                            || normalizedValue.EndsWith(
                                desiredSuffix,
                                StringComparison.OrdinalIgnoreCase)
                            || (desiredMode == "CY"
                                && normalizedText.Contains(
                                    "CONTAINER YARD",
                                    StringComparison.OrdinalIgnoreCase));

                        // Never silently choose Store Door when CY was requested.
                        if (!isDesiredMode)
                            continue;

                        var score = MatchScore(
                            expectedTokens,
                            Tokens($"{text} {value}"));

                        if (score < 0)
                            continue;

                        candidates.Add((
                            candidate,
                            score,
                            text.Length));
                    }
                    catch (PlaywrightException)
                    {
                        // The SPA can replace individual options while typing.
                    }
                }

                foreach (var candidate in candidates
                             .OrderByDescending(x => x.Score)
                             .ThenBy(x => x.LabelLength))
                {
                    try
                    {
                        await candidate.Locator.ClickAsync(
                            new LocatorClickOptions { Timeout = 1_750 });

                        return true;
                    }
                    catch (PlaywrightException)
                    {
                        try
                        {
                            await candidate.Locator.ClickAsync(
                                new LocatorClickOptions
                                {
                                    Timeout = 1_750,
                                    Force = true
                                });

                            return true;
                        }
                        catch (PlaywrightException)
                        {
                            // Try the next matching option.
                        }
                    }
                }
            }

            await Task.Delay(150, cancellationToken);
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

    public static async Task<bool> SelectTypeaheadOptionAsync(
        IPage page,
        IReadOnlyCollection<string> expectedValues,
        CancellationToken cancellationToken,
        int timeoutMs = 5_000,
        bool exactOnly = false)
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
                            SelectTypeaheadOptionScript,
                            new
                            {
                                expectedValues = expectedValues.ToArray(),
                                exactOnly
                            }))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while Maersk re-renders the typeahead.
                }
            }

            await Task.Delay(150, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SelectContainerTypeAsync(
        IPage page,
        string label,
        string code,
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
                    var clicked = await frame.EvaluateAsync<bool>(
                        SelectContainerTypeScript,
                        new { label, code });

                    if (!clicked)
                        continue;

                    // Give the web component time to commit the selected code and
                    // enable the number-of-containers stepper.
                    await Task.Delay(300, cancellationToken);
                    return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while the custom dropdown is rendering.
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> SetContainerQuantityAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
        int quantity,
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
                            SetContainerQuantityScript,
                            new { quantity }))
                        return true;
                }
                catch (PlaywrightException)
                {
                    // Retry while the number stepper is being enabled.
                }
            }

            await Task.Delay(200, cancellationToken);
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

    public static async Task<string?> ReadInteractiveHcaptchaChallengeAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        foreach (var frame in page.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!frame.Url.Contains(
                    "hcaptcha.com/captcha/",
                    StringComparison.OrdinalIgnoreCase)
                || !frame.Url.Contains(
                    "frame=challenge",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var frameElement = await frame.FrameElementAsync();
                var visible = await frameElement.EvaluateAsync<bool>(
                    """
                    element => {
                        const style = window.getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.display !== 'none'
                            && style.visibility !== 'hidden'
                            && Number(style.opacity || '1') > 0
                            && rect.width > 0
                            && rect.height > 0;
                    }
                    """);

                if (!visible)
                    continue;

                var bodyText = (await frame.Locator("body").InnerTextAsync()).Trim();

                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    return bodyText.Length <= 500
                        ? bodyText
                        : bodyText[..500];
                }

                return "Interactive hCaptcha challenge is active.";
            }
            catch (PlaywrightException)
            {
                return "Interactive hCaptcha challenge is active.";
            }
        }

        return null;
    }

    public static async Task<bool> WaitForInteractiveHcaptchaToClearAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReadInteractiveHcaptchaChallengeAsync(
                    page,
                    cancellationToken) is null)
                return true;

            await Task.Delay(500, cancellationToken);
        }

        return await ReadInteractiveHcaptchaChallengeAsync(
                   page,
                   cancellationToken) is null;
    }

    public static async Task<bool> ClickVisibleActionByTextAsync(
        IPage page,
        IReadOnlyCollection<string> labels,
        CancellationToken cancellationToken,
        int timeoutMs = 10_000,
        bool force = false)
    {
        static string Normalize(string? value)
            => string.Join(
                " ",
                (value ?? string.Empty)
                    .Split(
                        [' ', '\t', '\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToLowerInvariant();

        var wanted = labels
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (wanted.Length == 0)
            return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var frame in page.Frames)
            {
                ILocator candidates;

                try
                {
                    // Playwright CSS locators pierce open shadow roots. Using
                    // Locator.ClickAsync generates a trusted browser pointer event,
                    // unlike HTMLElement.click(), which newer Maersk MDS controls
                    // can ignore without throwing.
                    candidates = frame.Locator(
                        "button,mc-button,input[type='submit'],input[type='button'],[role='button']");

                    var count = await candidates.CountAsync();
                    var matches = new List<(ILocator Locator, int Rank, int Length)>();

                    for (var index = 0; index < count; index++)
                    {
                        var candidate = candidates.Nth(index);

                        if (!await candidate.IsVisibleAsync(
                                new LocatorIsVisibleOptions { Timeout = 500 }))
                            continue;

                        var ariaDisabled = await candidate.GetAttributeAsync(
                            "aria-disabled",
                            new LocatorGetAttributeOptions { Timeout = 500 });
                        var disabled = await candidate.GetAttributeAsync(
                            "disabled",
                            new LocatorGetAttributeOptions { Timeout = 500 });

                        if (disabled is not null
                            || string.Equals(
                                ariaDisabled,
                                "true",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        string text;

                        try
                        {
                            text = Normalize(await candidate.InnerTextAsync(
                                new LocatorInnerTextOptions { Timeout = 500 }));
                        }
                        catch (PlaywrightException)
                        {
                            text = string.Empty;
                        }

                        if (text.Length == 0)
                        {
                            text = Normalize(
                                await candidate.GetAttributeAsync(
                                    "aria-label",
                                    new LocatorGetAttributeOptions { Timeout = 500 })
                                ?? await candidate.GetAttributeAsync(
                                    "value",
                                    new LocatorGetAttributeOptions { Timeout = 500 }));
                        }

                        if (text.Length == 0)
                            continue;

                        var exact = wanted.Any(x => text == x);
                        var contains = exact || wanted.Any(x => text.Contains(x, StringComparison.Ordinal));

                        if (!contains)
                            continue;

                        matches.Add((
                            candidate,
                            exact ? 2 : 1,
                            text.Length));
                    }

                    foreach (var match in matches
                                 .OrderByDescending(x => x.Rank)
                                 .ThenBy(x => x.Length))
                    {
                        try
                        {
                            await match.Locator.ScrollIntoViewIfNeededAsync(
                                new LocatorScrollIntoViewIfNeededOptions
                                {
                                    Timeout = Math.Min(timeoutMs, 2_000)
                                });

                            await match.Locator.ClickAsync(
                                new LocatorClickOptions
                                {
                                    Timeout = Math.Min(timeoutMs, 3_000),
                                    Force = force
                                });

                            return true;
                        }
                        catch (PlaywrightException)
                        {
                            // Maersk can replace a web component between locating
                            // and clicking it. Try the next candidate/re-render.
                        }
                    }
                }
                catch (PlaywrightException)
                {
                    // Retry while the booking SPA re-renders.
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
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
