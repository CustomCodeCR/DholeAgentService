using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Browser;

public sealed class PlaywrightBrowserManager(IOptions<BrowserOptions> options) : IBrowserManager
{
    private readonly BrowserOptions _options = options.Value;

    public async Task<IBrowserSession> OpenPersistentAsync(
        BrowserProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(profile.StoragePath);

        cancellationToken.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync();

        try
        {
            var context = await playwright.Chromium.LaunchPersistentContextAsync(
                profile.StoragePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = _options.Headless,
                    Timeout = _options.DefaultTimeoutMs,
                    Args =
                    [
                        "--disable-dev-shm-usage",
                        "--no-sandbox",
                        "--disable-setuid-sandbox"
                    ]
                });

            context.SetDefaultTimeout(_options.DefaultTimeoutMs);
            context.SetDefaultNavigationTimeout(_options.DefaultTimeoutMs);
            return new PlaywrightBrowserSession(playwright, context);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }
}
