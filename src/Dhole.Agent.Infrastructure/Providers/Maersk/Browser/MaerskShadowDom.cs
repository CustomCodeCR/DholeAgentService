using System.Text.Json;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

internal static class MaerskShadowDom
{
    private const string FindVisibleElementScript = """
        selectors => {
            const isVisible = element => {
                if (!(element instanceof Element)) return false;
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none'
                    && style.visibility !== 'hidden'
                    && style.opacity !== '0'
                    && rect.width > 0
                    && rect.height > 0;
            };

            const find = root => {
                for (const selector of selectors) {
                    for (const element of root.querySelectorAll(selector)) {
                        if (isVisible(element)) return element;
                    }
                }

                for (const host of root.querySelectorAll('*')) {
                    if (!host.shadowRoot) continue;
                    const nested = find(host.shadowRoot);
                    if (nested) return nested;
                }

                return null;
            };

            return find(document);
        }
        """;

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
                IJSHandle? handle = null;
                try
                {
                    handle = await frame.EvaluateHandleAsync(FindVisibleElementScript, selectors.ToArray());
                    var element = handle.AsElement();
                    if (element is null) continue;

                    await element.FillAsync(value, new ElementHandleFillOptions { Timeout = 5_000 });
                    return true;
                }
                catch (PlaywrightException)
                {
                    // Login/search SPAs can replace the component while redirects finish.
                }
                finally
                {
                    if (handle is not null)
                        await handle.DisposeAsync();
                }
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
                IJSHandle? handle = null;
                try
                {
                    handle = await frame.EvaluateHandleAsync(FindVisibleElementScript, selectors.ToArray());
                    var element = handle.AsElement();
                    if (element is null) continue;

                    await element.ClickAsync(new ElementHandleClickOptions { Timeout = 5_000 });
                    return true;
                }
                catch (PlaywrightException)
                {
                    // Try again while the SPA settles.
                }
                finally
                {
                    if (handle is not null)
                        await handle.DisposeAsync();
                }
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
                IJSHandle? handle = null;
                try
                {
                    handle = await frame.EvaluateHandleAsync(FindVisibleElementScript, selectors.ToArray());
                    var element = handle.AsElement();
                    if (element is null) continue;

                    await element.SelectOptionAsync(
                        new[] { new SelectOptionValue { Label = label } },
                        new ElementHandleSelectOptionOptions { Timeout = 5_000 });
                    return true;
                }
                catch (PlaywrightException)
                {
                    // Try the next candidate/frame.
                }
                finally
                {
                    if (handle is not null)
                        await handle.DisposeAsync();
                }
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
