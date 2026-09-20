using Dhole.Agent.Application.Abstractions.Runtime;
using Microsoft.Playwright;

namespace Dhole.Agent.Infrastructure.Browser;

public sealed class PlaywrightBrowserSession(IPlaywright playwright, IBrowserContext context) : IBrowserSession
{
    public IBrowserContext Context { get; } = context;

    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        playwright.Dispose();
    }
}
