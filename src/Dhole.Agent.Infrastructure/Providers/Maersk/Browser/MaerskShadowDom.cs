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

                for (const element of root.querySelectorAll("button,input[type='submit'],mc-button")) {
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

    private const string DescribeScript = """
        () => {
            const result = {
                title: document.title || '',
                bodyText: (document.body?.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 700),
                mdsInputs: []
            };

            const visit = root => {
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
                if (await TryActOnVisibleLocatorAsync(
                        frame,
                        selectors,
                        locator => locator.FillAsync(
                            value,
                            new LocatorFillOptions { Timeout = 5_000 })))
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

    public static async Task<bool> ClickFirstAsync(
        IPage page,
        IReadOnlyCollection<string> selectors,
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
                        locator => locator.ClickAsync(
                            new LocatorClickOptions { Timeout = 5_000 })))
                    return true;
            }

            await Task.Delay(250, cancellationToken);
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
