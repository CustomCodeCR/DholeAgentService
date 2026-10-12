using Dhole.Agent.Domain.Agents;

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
           && string.Equals(execution.InputJson, InputJson, StringComparison.Ordinal);
}
