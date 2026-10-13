namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

/// <summary>
/// Recognizes Playwright's positioning failure, not login/edge/verification
/// failures. Keyboard recovery is permitted only for the exact matched option.
/// </summary>
public static class MaerskOptionViewportRecovery
{
    public static bool IsOutsideViewport(string? playwrightMessage)
        => !string.IsNullOrWhiteSpace(playwrightMessage)
           && (playwrightMessage.Contains("outside of the viewport", StringComparison.OrdinalIgnoreCase)
               || playwrightMessage.Contains("outside the viewport", StringComparison.OrdinalIgnoreCase)
               || playwrightMessage.Contains("not in the viewport", StringComparison.OrdinalIgnoreCase));
}
