using Dhole.Agent.Domain.Agents;
using System.Text.Json;

namespace Dhole.Agent.Application.Runtime;

/// <summary>
/// Sole, operator-initiated exception to Maersk's *dispatch pause*.
/// This operation only verifies the original browser session. It cannot search
/// rates, alter the circuit, retry automatically, or rotate the Chromium profile.
/// </summary>
public static class MaerskSessionVerification
{
    public const string DefinitionCode = "MAERSK_SESSION_VERIFY";
    public const string InputJson = "{\"maerskSessionVerification\":true}";

    public static bool IsExplicit(
        AgentExecution execution, AgentDefinition definition, AgentProvider provider)
        => provider.Code.Equals("MAERSK", StringComparison.OrdinalIgnoreCase)
           && definition.ProviderId == provider.Id
           && definition.Code == DefinitionCode
           && definition.ActionType == AgentActionType.Authenticate
           && execution.AgentDefinitionId == definition.Id
           && execution.ProviderId == provider.Id
           && execution.ExecutionType == AgentExecutionType.Manual
           && execution.MaxAttempts == 1
           && HasExactVerificationMarker(execution.InputJson);

    // An operator-only verification may inspect the blocked original profile
    // while holding its lifetime lock. Rate searches, scheduled executions,
    // technical resets, and all other actions remain denied.
    public static bool MayInspectBlockedOriginalProfile(
        bool isExplicitManualVerification, BrowserProfileStatus? status)
        => isExplicitManualVerification && status == BrowserProfileStatus.Blocked;

    // EF Core stores execution.input_json as PostgreSQL jsonb. PostgreSQL
    // normalizes whitespace/key layout (for example, {"flag": true}), so an
    // ordinal string comparison to serialized JSON would reject legitimate
    // operator-requested verification tasks forever while the circuit is Open.
    // Parse the semantic marker instead, but reject any additional input
    // fields to keep this strictly authentication-only.
    private static bool HasExactVerificationMarker(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var properties = doc.RootElement.EnumerateObject();
            if (!properties.MoveNext()) return false;
            var property = properties.Current;
            if (property.Name != "maerskSessionVerification"
                || property.Value.ValueKind != JsonValueKind.True)
                return false;

            // A second property (including duplicate marker keys) disqualifies
            // any job from bypassing the normal provider circuit pause.
            return !properties.MoveNext();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
