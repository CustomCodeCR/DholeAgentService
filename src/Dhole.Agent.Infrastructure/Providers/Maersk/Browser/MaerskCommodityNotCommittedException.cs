namespace Dhole.Agent.Infrastructure.Providers.Maersk.Browser;

/// <summary>
/// The booking form has not acknowledged a commodity selection, so a batch
/// cannot reliably advance to its equipment searches. This is a form-state
/// problem, not a signal to rotate identities, sessions or environments.
/// </summary>
internal sealed class MaerskCommodityNotCommittedException(string commodity)
    : InvalidOperationException(
        "Maersk did not enable the container selector after selecting commodity '" +
        (commodity.Length > 120 ? commodity[..120] : commodity) +
        "'. Confirm a commodity from the Maersk suggestions and verify both " +
        "locations are committed as CY/CY. The remaining equipment searches " +
        "were stopped to avoid repeating the same invalid form state.")
{
}
