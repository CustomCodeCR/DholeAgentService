using CustomCodeFramework.Core.Results;

namespace Dhole.Agent.Application.Agents;

public static class AgentErrors
{
    public static readonly Error ProviderNotFound = new("Agent.ProviderNotFound","Agent provider not found.");
    public static readonly Error ProviderCodeExists = new("Agent.ProviderCodeExists","An agent provider with the same code already exists.");
    public static readonly Error DefinitionNotFound = new("Agent.DefinitionNotFound","Agent definition not found.");
    public static readonly Error DefinitionCodeExists = new("Agent.DefinitionCodeExists","An agent definition with the same code already exists.");
    public static readonly Error CredentialNotFound = new("Agent.CredentialNotFound","Agent credential not found.");
    public static readonly Error BrowserProfileNotFound = new("Agent.BrowserProfileNotFound","Browser profile not found.");
    public static readonly Error ScheduleNotFound = new("Agent.ScheduleNotFound","Agent schedule not found.");
    public static readonly Error ExecutionNotFound = new("Agent.ExecutionNotFound","Agent execution not found.");
}
